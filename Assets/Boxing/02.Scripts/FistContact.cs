// FistContact.cs
using System.Collections.Generic;
using UnityEngine;

public class FistContact : MonoBehaviour
{
    [HideInInspector] public BoxerAgent ownerAgent; // 타격자(나)

    // 너무 약한 접촉(비비기/지터)을 1차로 걸러서 이벤트 노이즈를 줄임
    [SerializeField] private float m_MinNotifyForce = 120f;

    [Header("De-duplication (Anti Rub / Anti Multi-collider Spam)")]
    [SerializeField] private float m_PerHandCooldown = 0.10f;
    [SerializeField] private float m_PerVictimCooldown = 0.18f;

    [Header("Anti-Tunneling (Conditional SphereCast NonAlloc)")]
    [SerializeField] private bool m_EnableAntiTunnelingCast = true;
    [SerializeField] private float m_CastRadius = 0.06f;
    [SerializeField] private LayerMask m_CastMask = ~0;
    [SerializeField] private float m_CastMinTravelDistance = 0.03f;
    [SerializeField] private float m_CastMinSpeed = 2.5f;
    [SerializeField] private int m_CastHitBufferSize = 8;

    private Rigidbody m_Rb;
    private bool m_HadCollisionSinceLastFixed;
    private Vector3 m_LastFixedPos;

    private float m_LastAnyContactTime = -999f;
    private readonly Dictionary<int, float> m_LastVictimContactTime = new Dictionary<int, float>(16);

    private RaycastHit[] m_CastHits;

    private void Awake()
    {
        m_Rb = GetComponent<Rigidbody>();

        // 주먹은 고속 이동이 흔하므로 Continuous Dynamic 권장(터널링 방지)
        if (m_Rb != null)
        {
            m_Rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            m_Rb.interpolation = RigidbodyInterpolation.Interpolate;
        }

        m_LastFixedPos = transform.position;
        m_CastHits = new RaycastHit[Mathf.Max(1, m_CastHitBufferSize)];
    }

    private void FixedUpdate()
    {
        if (!m_EnableAntiTunnelingCast || ownerAgent == null)
        {
            m_LastFixedPos = transform.position;
            m_HadCollisionSinceLastFixed = false;
            return;
        }

        // 지난 물리 스텝 동안의 이동량 기반으로, 터널링 가능성이 있을 때만 Cast 수행
        Vector3 currentPos = transform.position;
        Vector3 delta = currentPos - m_LastFixedPos;
        float dist = delta.magnitude;

        float speed = GetFistVelocitySafe().magnitude;
        bool shouldCast = dist >= m_CastMinTravelDistance && speed >= m_CastMinSpeed;

        // 같은 물리 스텝에 실제 Collision 콜백이 있었다면 Cast는 중복이므로 스킵
        if (shouldCast && !m_HadCollisionSinceLastFixed)
        {
            Vector3 dir = delta / Mathf.Max(dist, 1e-6f);

            int hitCount = Physics.SphereCastNonAlloc(
                origin: m_LastFixedPos,
                radius: m_CastRadius,
                direction: dir,
                results: m_CastHits,
                maxDistance: dist,
                layerMask: m_CastMask,
                queryTriggerInteraction: QueryTriggerInteraction.Ignore
            );

            for (int i = 0; i < hitCount; i++)
            {
                Collider col = m_CastHits[i].collider;
                if (col == null) continue;

                // 자기 몸 충돌 무시
                if (col.transform.root == transform.root) continue;

                ProcessHitEvent(
                    victimRoot: col.transform.root,
                    victimCollider: col,
                    contactPoint: m_CastHits[i].point,
                    contactNormal: m_CastHits[i].normal,
                    isFromCast: true,
                    collision: null
                );
            }
        }

        m_LastFixedPos = currentPos;
        m_HadCollisionSinceLastFixed = false;
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (ownerAgent == null) return;
        if (collision.transform.root == transform.root) return;

        m_HadCollisionSinceLastFixed = true;

        Vector3 p = transform.position;
        Vector3 n = Vector3.up;
        if (collision.contactCount > 0)
        {
            var c = collision.GetContact(0);
            p = c.point;
            n = c.normal;
        }

        ProcessHitEvent(
            victimRoot: collision.transform.root,
            victimCollider: collision.collider,
            contactPoint: p,
            contactNormal: n,
            isFromCast: false,
            collision: collision
        );
    }

    private void ProcessHitEvent(
        Transform victimRoot,
        Collider victimCollider,
        Vector3 contactPoint,
        Vector3 contactNormal,
        bool isFromCast,
        Collision collision
    )
    {
        if (ownerAgent == null) return;
        if (victimRoot == null || victimCollider == null) return;

        // 손 단위 쿨다운(지터/비비기 중복 이벤트 방지)
        if (Time.time - m_LastAnyContactTime < m_PerHandCooldown) return;

        // 환경 처리(벽/바닥)
        if (victimCollider.CompareTag(BoxerAgent.TagWall) || victimCollider.CompareTag(BoxerAgent.TagGround))
        {
            float envForce = EstimateHitForce(isFromCast, collision);
            if (envForce < m_MinNotifyForce) return;

            m_LastAnyContactTime = Time.time;
            ownerAgent.RegisterEnvironmentStrike(envForce, victimCollider);
            return;
        }

        // 상대 에이전트
        BoxerAgent victim = victimRoot.GetComponent<BoxerAgent>();
        if (victim == null || victim == ownerAgent) return;

        // 상대별 쿨다운(다중 콜라이더/비비기/연속 접촉으로 득점하는 꼼수 방지)
        int victimId = victim.GetInstanceID();
        if (m_LastVictimContactTime.TryGetValue(victimId, out float lastT))
        {
            if (Time.time - lastT < m_PerVictimCooldown) return;
        }

        float hitForce = EstimateHitForce(isFromCast, collision);
        if (hitForce < m_MinNotifyForce) return;

        m_LastAnyContactTime = Time.time;
        m_LastVictimContactTime[victimId] = Time.time;

        Vector3 fistVel = GetFistVelocitySafe();

        // 룰 판정/보상/데미지는 피해자 쪽(ReceivePunch)에서 단일화
        victim.ReceivePunch(
            attacker: ownerAgent,
            attackerFist: this,
            victimCollider: victimCollider,
            hitForce: hitForce,
            contactPoint: contactPoint,
            contactNormal: contactNormal,
            relativeVelocity: (collision != null ? collision.relativeVelocity : Vector3.zero),
            fistVelocity: fistVel
        );

        // impulse 기반(실제 충돌)과 cast 근사(m*v/dt)의 스케일 차이를 EMA로 캘리브레이션
        if (!isFromCast && collision != null)
        {
            float realForce = collision.impulse.magnitude / Mathf.Max(Time.fixedDeltaTime, 1e-4f);
            float approxForce = ApproxForceFromMVOverDt();
            ownerAgent.UpdateCastForceCalibration(realForce, approxForce);
        }
    }

    private float EstimateHitForce(bool isFromCast, Collision collision)
    {
        // Collision 이벤트는 impulse 기반이 가장 신뢰도 높음
        if (!isFromCast && collision != null)
        {
            return collision.impulse.magnitude / Mathf.Max(Time.fixedDeltaTime, 1e-4f);
        }

        // Cast 기반은 m*v/dt 근사에, 에이전트가 누적 학습한 보정 스케일을 곱함
        float approx = ApproxForceFromMVOverDt();
        float scale = ownerAgent != null ? ownerAgent.GetCastForceScale() : 1f;
        return approx * scale;
    }

    private float ApproxForceFromMVOverDt()
    {
        if (m_Rb == null) return 0f;

        float v = GetFistVelocitySafe().magnitude;
        float impulseApprox = m_Rb.mass * v;
        return impulseApprox / Mathf.Max(Time.fixedDeltaTime, 1e-4f);
    }

    public Vector3 GetFistVelocitySafe()
    {
        return m_Rb != null ? m_Rb.linearVelocity : Vector3.zero;
    }
}
