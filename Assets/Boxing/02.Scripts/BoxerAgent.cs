// BoxerAgent.cs
using System;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using Unity.MLAgentsExamples;
using BodyPart = Unity.MLAgentsExamples.BodyPart;
using Random = UnityEngine.Random;

public class BoxerAgent : Agent
{
    // ========= Tags =========
    public const string TagHead = "BoxingHead";
    public const string TagTorso = "BoxingTorso";
    public const string TagGuard = "BoxingGuard";
    public const string TagIllegal = "BoxingIllegal";
    public const string TagWall = "Wall";
    public const string TagGround = "Ground"; // 환경 충돌용(바닥), GroundContact는 별도 태그 사용 가능

    [Header("Opponent / Target")]
    public Transform targetTransform; // 상대 hips
    public BoxerAgent opponentAgent;  // 승패 동기화용(권장)

    [Header("Ring / Boundary Awareness")]
    public BoxCollider ringBounds;
    [SerializeField] private float m_RingOutMargin = 0.05f;
    [SerializeField] private float m_RingOutPenalty = -1.0f;

    [SerializeField] private float m_EdgeDangerThreshold = 0.18f;
    [SerializeField] private float m_EdgeCampingPenalty = -0.0025f;

    [Header("Body Parts")]
    public Transform hips;
    public Transform chest;
    public Transform spine;
    public Transform head;
    public Transform thighL;
    public Transform shinL;
    public Transform footL;
    public Transform thighR;
    public Transform shinR;
    public Transform footR;
    public Transform armL;
    public Transform forearmL;
    public Transform handL;
    public Transform armR;
    public Transform forearmR;
    public Transform handR;

    private OrientationCubeController m_OrientationCube;
    private JointDriveController m_JdController;

    // ========= Modern Boxing Rules / Scoring =========
    [Header("Scoring Gate")]
    [SerializeField] private float m_BeltLineYOffset = 0.02f;
    [SerializeField] private float m_MinScoringForce = 180f;
    [SerializeField] private float m_MaxScoringForce = 900f;

    [SerializeField] private float m_MinApproachSpeed = 1.2f;
    [SerializeField] private float m_MinGloveDirectionCos = 0.25f;

    [SerializeField] private float m_AttackerGlobalHitCooldown = 0.20f;

    [Header("Rewards")]
    [SerializeField] private float m_RewardHead = 1.0f;
    [SerializeField] private float m_RewardTorso = 0.5f;

    [SerializeField] private float m_RewardBlockedToDefender = 0.03f;
    [SerializeField] private float m_PenaltyBlockedToAttacker = -0.01f;

    [SerializeField] private float m_PenaltyFoul = -0.35f;
    [SerializeField] private float m_PenaltyEnvironmentStrike = -0.05f;

    [SerializeField] private float m_VictimPenaltyFactor = 0.8f;

    [Header("DQ / Fouls")]
    [SerializeField] private int m_MaxFoulsBeforeDQ = 3;

    // ========= Health / KO-TKO / Round =========
    [Header("Damage / KO-TKO")]
    [SerializeField] private float m_MaxHealth = 100f;
    [SerializeField] private float m_DamageHeadMultiplier = 1.2f;
    [SerializeField] private float m_DamageTorsoMultiplier = 1.0f;
    [SerializeField] private float m_KOHealthThreshold = 0f;

    [SerializeField] private float m_KnockdownHipsHeight = 0.55f;
    [SerializeField] private float m_KnockdownHoldSeconds = 1.2f;

    [Header("Round / Decision")]
    [SerializeField] private float m_RoundSeconds = 45f;
    [SerializeField] private float m_DecisionWinReward = 1.0f;
    [SerializeField] private float m_DecisionLoseReward = -1.0f;

    // ========= Action Smoothing (Annealed) =========
    [Header("Action Smoothing (Annealed)")]
    [SerializeField] private float m_SmoothingStartAlpha = 0.85f;
    [SerializeField] private float m_SmoothingEndAlpha = 0.35f;
    [SerializeField] private float m_MaxDeltaStart = 0.35f;
    [SerializeField] private float m_MaxDeltaEnd = 0.12f;
    [SerializeField] private int m_SmoothingAnnealEpisodes = 200;

    [SerializeField] private float m_ActionDeltaPenaltyScale = 0.0008f;
    [SerializeField] private float m_JointAngularVelocityPenaltyScale = 0.00015f;

    // ========= Stability (CoM + Momentum) =========
    [Header("Stability")]
    [SerializeField] private float m_SupportFootRadius = 0.14f;
    [SerializeField] private float m_HandsOnGroundPenalty = -0.0025f;
    [SerializeField] private float m_UnstablePenalty = -0.0020f;
    [SerializeField] private float m_MomentumToEdgePenaltyScale = -0.0018f;

    // ========= Fatigue & Stun =========
    [Header("Fatigue & Stun")]
    [SerializeField] private float m_StaminaDrainScale = 0.0020f;
    [SerializeField] private float m_StaminaRecoveryPerSecond = 0.08f;
    [SerializeField] private float m_MinPerformanceMultiplier = 0.35f;

    [SerializeField] private float m_StunHeadForceThreshold01 = 0.75f;
    [SerializeField] private float m_StunSeconds = 0.35f;

    [SerializeField] private float m_MaxForceLimitSmooth = 0.10f;

    // ========= Cast Force Calibration =========
    [Header("Cast Force Calibration")]
    [SerializeField] private float m_CastForceScaleEmaAlpha = 0.05f;
    [SerializeField] private float m_CastForceScaleClampMin = 0.25f;
    [SerializeField] private float m_CastForceScaleClampMax = 4.0f;

    private float m_CastForceScale = 1.0f;

    // ========= Reset / Anti-Spin Safety =========
    [Header("Reset Safety (Anti Spin)")]
    [SerializeField] private float m_ResetHeightLift = 0.08f;
    [SerializeField] private float m_MaxAngularVelocityClamp = 12f;
    [SerializeField] private float m_AngularDrag = 0.08f;

    private Rigidbody[] m_AllRigidbodies;
    private float m_BaseMaxJointForceLimit;

    // ========= Internal State =========
    private float m_Health;
    private float m_Score;
    private int m_FoulCount;

    private float m_LastScoringTime;
    private bool m_MatchOver;

    private float m_DownStartTime = -1f;
    private float m_RoundTimer;

    private float m_Stamina01;
    private float m_StunUntilTime;
    private float m_PerformanceMultiplier;

    private int m_EpisodeCount;

    // “물리 콜백에서 EndEpisode 직접 호출 금지”를 위한 종료 요청 플래그
    private bool m_EndEpisodeRequested;
    private float m_EndEpisodeAtTime;

    // 액션 버퍼
    private float[] m_LastRawActions;
    private float[] m_SmoothedActions;
    private float[] m_WorkActions;

    // 연속 액션 차원(원본 사용자 코드 기준: 39)
    // (환경의 BehaviorParameters Continuous Action Size가 이 값과 일치해야 합니다)
    private const int kRequiredContinuousActions = 39;

    public override void Initialize()
    {
        m_OrientationCube = GetComponentInChildren<OrientationCubeController>();
        m_JdController = GetComponent<JointDriveController>();

        SetupBodyParts();

        // 주먹 센서(기존 스크립트 재사용)
        var leftFist = handL.GetComponent<FistContact>() ?? handL.gameObject.AddComponent<FistContact>();
        leftFist.ownerAgent = this;

        var rightFist = handR.GetComponent<FistContact>() ?? handR.gameObject.AddComponent<FistContact>();
        rightFist.ownerAgent = this;

        m_AllRigidbodies = GetComponentsInChildren<Rigidbody>(true);
        m_BaseMaxJointForceLimit = m_JdController.maxJointForceLimit;

        // “스핀 폭주” 마지막 방어선(근본 해결은 리셋/종료 흐름이지만, 안전상 상한을 걸어둠)
        ApplyRigidBodySafetyLimits();
    }

    private void SetupBodyParts()
    {
        m_JdController.SetupBodyPart(hips);
        m_JdController.SetupBodyPart(chest);
        m_JdController.SetupBodyPart(spine);
        m_JdController.SetupBodyPart(head);
        m_JdController.SetupBodyPart(thighL);
        m_JdController.SetupBodyPart(shinL);
        m_JdController.SetupBodyPart(footL);
        m_JdController.SetupBodyPart(thighR);
        m_JdController.SetupBodyPart(shinR);
        m_JdController.SetupBodyPart(footR);
        m_JdController.SetupBodyPart(armL);
        m_JdController.SetupBodyPart(forearmL);
        m_JdController.SetupBodyPart(handL);
        m_JdController.SetupBodyPart(armR);
        m_JdController.SetupBodyPart(forearmR);
        m_JdController.SetupBodyPart(handR);
    }

    // ======= Safe EndEpisode 요청 API (충돌 콜백에서 직접 EndEpisode 금지) =======
    public void RequestEndEpisode(float delaySeconds = 0f)
    {
        // 여러 번 호출되더라도 더 빠른 종료 시점을 유지
        float t = Time.time + Mathf.Max(0f, delaySeconds);

        if (!m_EndEpisodeRequested || t < m_EndEpisodeAtTime)
        {
            m_EndEpisodeRequested = true;
            m_EndEpisodeAtTime = t;
        }
    }

    public override void OnEpisodeBegin()
    {
        m_EpisodeCount++;

        m_Health = m_MaxHealth;
        m_Score = 0f;
        m_FoulCount = 0;

        m_LastScoringTime = -999f;
        m_MatchOver = false;

        m_DownStartTime = -1f;
        m_RoundTimer = 0f;

        m_Stamina01 = 1f;
        m_StunUntilTime = -999f;
        m_PerformanceMultiplier = 1f;

        m_EndEpisodeRequested = false;
        m_EndEpisodeAtTime = 0f;

        // ---- 리셋 안정화 핵심 루틴 (Anti Spin) ----
        // 1) 전부 kinematic으로 고정(물리 반응 중지)
        SetAllBodiesKinematic(true);

        // 2) 바디파트 포즈 리셋(관절 목표/초기 자세로)
        foreach (var bodyPart in m_JdController.bodyPartsDict.Values)
        {
            bodyPart.Reset(bodyPart);
        }

        // 3) 루트 위치를 살짝 들어서(바닥 관통 방지) 리셋 시작
        //    (현재 위치 기반으로 들어올림; 스폰 랜덤은 외부 매니저에서 하시는 것을 권장)
        Vector3 p = transform.position;
        transform.position = new Vector3(p.x, p.y + m_ResetHeightLift, p.z);

        // 4) 루트 회전은 hips만 돌리지 말고 전체(루트) 회전 사용 가능
        //    (hips만 돌리면 관절 비틀림 → 큰 토크 → 스핀/폭발 유발 가능)
        SetRootYaw(Random.Range(0f, 360f));

        // 5) 트랜스폼 반영을 물리에 동기화
        Physics.SyncTransforms();

        // 6) 속도/각속도 완전 제거(잔류 회전 제거)
        ZeroAllBodyVelocities();

        // 7) 다시 물리 활성화
        SetAllBodiesKinematic(false);

        // 8) 수면 상태로 시작(초기 “회전 에너지” 제거)
        SleepAllBodies();

        // 9) 액션/스무딩 버퍼 리셋(과거 액션 잔류 시 급토크 유발)
        EnsureActionBufferSize(kRequiredContinuousActions);
        ResetActionBuffers();

        // maxJointForceLimit은 즉시 바꾸지 않고 FixedUpdate에서 목표로 서서히 수렴
        m_JdController.maxJointForceLimit = m_BaseMaxJointForceLimit;

        UpdateOrientationObjects();
    }

    private void FixedUpdate()
    {
        // (중요) 충돌 콜백에서 들어온 종료 요청은 여기서 처리
        // 물리 스텝 중간에 EndEpisode()를 호출하면 “스핀/폭발”이 잘 발생하므로 금지
        if (m_EndEpisodeRequested && Time.time >= m_EndEpisodeAtTime)
        {
            m_EndEpisodeRequested = false;
            EndEpisode();
            return;
        }

        if (m_MatchOver) return;

        UpdateOrientationObjects();
        UpdateFatigueAndPerformance();

        AddReward(ComputeAngularVelocityPenalty());

        // 링아웃은 큰 페널티 + 경기 종료
        if (IsOutOfRing())
        {
            AddReward(m_RingOutPenalty);

            if (opponentAgent != null)
            {
                EndMatch(opponentAgent, this, "Ring Out");
            }
            else
            {
                // 상대가 없으면 자기만 종료
                m_MatchOver = true;
                RequestEndEpisode();
            }
            return;
        }

        // 경계 캠핑 패널티(보상 세척 방지 원칙: 중앙 보상 대신 경계 감점)
        float minBoundary01 = GetMinBoundaryDistance01();
        if (minBoundary01 < m_EdgeDangerThreshold)
        {
            float k = Mathf.Clamp01((m_EdgeDangerThreshold - minBoundary01) / Mathf.Max(m_EdgeDangerThreshold, 1e-3f));
            AddReward(m_EdgeCampingPenalty * (0.5f + 0.5f * k));
        }

        // 거리 shaping도 “보상”보다 “기준 미달 감점” 위주로
        if (targetTransform != null)
        {
            float dist = Vector3.Distance(hips.position, targetTransform.position);
            if (dist > 4.5f) AddReward(-0.0035f);
            if (dist < 0.75f) AddReward(-0.0025f);
        }

        // 안정도/기어다니기 억제(손 접지 감점)
        ComputeStability2D(out float stability01, out float signedDist, out Vector2 supportOutDir);

        if (signedDist < 0f)
        {
            AddReward(m_UnstablePenalty);

            // CoM이 바깥으로 나가는 방향 속도 성분이 크면 추가 감점(넘어지기 전 대비)
            Vector3 comVel = ComputeCenterOfMassVelocityWorld();
            Vector2 comVel2 = new Vector2(comVel.x, comVel.z);
            float outwardSpeed = Vector2.Dot(comVel2, supportOutDir);
            if (outwardSpeed > 0f)
            {
                AddReward(m_MomentumToEdgePenaltyScale * outwardSpeed);
            }
        }

        var bp = m_JdController.bodyPartsDict;
        if (bp[handL].groundContact.touchingGround || bp[handR].groundContact.touchingGround)
        {
            AddReward(m_HandsOnGroundPenalty);
        }

        // 다운(TKO) 판정: hips 높이 + 지속시간
        if (hips.position.y < m_KnockdownHipsHeight)
        {
            if (m_DownStartTime < 0f) m_DownStartTime = Time.time;

            if (Time.time - m_DownStartTime > m_KnockdownHoldSeconds)
            {
                if (opponentAgent != null) EndMatch(opponentAgent, this, "TKO (Down)");
                else { m_MatchOver = true; RequestEndEpisode(); }
                return;
            }

            AddReward(-0.004f);
        }
        else
        {
            m_DownStartTime = -1f;
        }

        // 라운드 종료 판정(점수/체력 기반 단순화)
        m_RoundTimer += Time.fixedDeltaTime;
        if (m_RoundTimer >= m_RoundSeconds)
        {
            if (opponentAgent != null)
            {
                if (m_Score > opponentAgent.m_Score) EndMatch(this, opponentAgent, "Decision (Score)");
                else if (m_Score < opponentAgent.m_Score) EndMatch(opponentAgent, this, "Decision (Score)");
                else
                {
                    if (m_Health > opponentAgent.m_Health) EndMatch(this, opponentAgent, "Decision (Health)");
                    else if (m_Health < opponentAgent.m_Health) EndMatch(opponentAgent, this, "Decision (Health)");
                    else
                    {
                        // 무승부
                        m_MatchOver = true;
                        opponentAgent.m_MatchOver = true;
                        RequestEndEpisode();
                        opponentAgent.RequestEndEpisode();
                    }
                }
            }
            else
            {
                m_MatchOver = true;
                RequestEndEpisode();
            }
        }
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        if (targetTransform != null)
        {
            Vector3 toTarget = targetTransform.position - hips.position;
            sensor.AddObservation(m_OrientationCube.transform.InverseTransformDirection(toTarget));
            sensor.AddObservation(toTarget.magnitude);

            Vector3 toTargetN = toTarget.sqrMagnitude > 1e-6f ? toTarget.normalized : Vector3.forward;
            sensor.AddObservation(Quaternion.FromToRotation(head.forward, toTargetN));
        }
        else
        {
            sensor.AddObservation(Vector3.zero);
            sensor.AddObservation(0f);
            sensor.AddObservation(Quaternion.identity);
        }

        sensor.AddObservation(m_OrientationCube.transform.InverseTransformDirection(GetAvgVelocity()));
        sensor.AddObservation(Quaternion.FromToRotation(hips.forward, m_OrientationCube.transform.forward));

        AddRingObservations(sensor);

        Vector3 com = ComputeCenterOfMassWorld();
        Vector3 comVel = ComputeCenterOfMassVelocityWorld();

        Vector3 comLocal = m_OrientationCube.transform.InverseTransformDirection(com - hips.position);
        Vector3 comVelLocal = m_OrientationCube.transform.InverseTransformDirection(comVel);

        sensor.AddObservation(comLocal.x);
        sensor.AddObservation(comLocal.z);
        sensor.AddObservation(comVelLocal.x);
        sensor.AddObservation(comVelLocal.z);

        ComputeStability2D(out float stability01, out float signedDist, out _);
        sensor.AddObservation(stability01);
        sensor.AddObservation(Mathf.Clamp(signedDist, -1f, 1f));

        sensor.AddObservation(Mathf.Clamp01(m_Health / Mathf.Max(1f, m_MaxHealth)));
        sensor.AddObservation(m_Stamina01);
        sensor.AddObservation(m_PerformanceMultiplier);
        sensor.AddObservation(IsStunned() ? 1f : 0f);

        if (opponentAgent != null)
        {
            sensor.AddObservation(Mathf.Clamp01(opponentAgent.m_Health / Mathf.Max(1f, opponentAgent.m_MaxHealth)));
            sensor.AddObservation(opponentAgent.m_Stamina01);
            sensor.AddObservation(opponentAgent.m_PerformanceMultiplier);
        }
        else
        {
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
        }

        foreach (var part in m_JdController.bodyPartsList)
        {
            CollectObservationBodyPart(part, sensor);
        }
    }

    private void CollectObservationBodyPart(BodyPart bp, VectorSensor sensor)
    {
        sensor.AddObservation(bp.groundContact.touchingGround);
        sensor.AddObservation(m_OrientationCube.transform.InverseTransformDirection(bp.rb.linearVelocity));
        sensor.AddObservation(m_OrientationCube.transform.InverseTransformDirection(bp.rb.angularVelocity));
        sensor.AddObservation(m_OrientationCube.transform.InverseTransformDirection(bp.rb.position - hips.position));

        if (bp.rb.transform != hips && bp.rb.transform != handL && bp.rb.transform != handR)
        {
            sensor.AddObservation(bp.rb.transform.localRotation);
            sensor.AddObservation(bp.currentStrength / Mathf.Max(m_JdController.maxJointForceLimit, 1e-3f));
        }
    }

    public override void OnActionReceived(ActionBuffers actionBuffers)
    {
        var a = actionBuffers.ContinuousActions;

        // 차원이 맞지 않으면 매핑이 깨짐(학습 자체가 오염되므로 즉시 리턴)
        if (a.Length < kRequiredContinuousActions) return;

        EnsureActionBufferSize(a.Length);

        AddReward(ComputeActionDeltaPenalty(a));

        float alpha = GetAnnealedAlpha();
        float maxDelta = GetAnnealedMaxDelta();

        float perfDeltaMul = Mathf.Lerp(0.65f, 1.0f, m_PerformanceMultiplier);
        if (IsStunned()) perfDeltaMul *= 0.6f;
        maxDelta *= perfDeltaMul;

        for (int i = 0; i < a.Length; i++)
        {
            float target = a[i];
            if (IsStunned()) target *= 0.2f;

            float prev = m_SmoothedActions[i];

            float delta = Mathf.Clamp(target - prev, -maxDelta, maxDelta);
            float limited = prev + delta;

            float smoothed = Mathf.Lerp(prev, limited, alpha);
            smoothed = Mathf.Clamp(smoothed, -1f, 1f);

            m_WorkActions[i] = smoothed;
            m_SmoothedActions[i] = smoothed;
        }

        var bp = m_JdController.bodyPartsDict;
        int idx = -1;

        // 회전 타겟
        bp[chest].SetJointTargetRotation(m_WorkActions[++idx], m_WorkActions[++idx], m_WorkActions[++idx]);
        bp[spine].SetJointTargetRotation(m_WorkActions[++idx], m_WorkActions[++idx], m_WorkActions[++idx]);

        bp[thighL].SetJointTargetRotation(m_WorkActions[++idx], m_WorkActions[++idx], 0);
        bp[thighR].SetJointTargetRotation(m_WorkActions[++idx], m_WorkActions[++idx], 0);
        bp[shinL].SetJointTargetRotation(m_WorkActions[++idx], 0, 0);
        bp[shinR].SetJointTargetRotation(m_WorkActions[++idx], 0, 0);
        bp[footR].SetJointTargetRotation(m_WorkActions[++idx], m_WorkActions[++idx], m_WorkActions[++idx]);
        bp[footL].SetJointTargetRotation(m_WorkActions[++idx], m_WorkActions[++idx], m_WorkActions[++idx]);

        bp[armL].SetJointTargetRotation(m_WorkActions[++idx], m_WorkActions[++idx], 0);
        bp[armR].SetJointTargetRotation(m_WorkActions[++idx], m_WorkActions[++idx], 0);
        bp[forearmL].SetJointTargetRotation(m_WorkActions[++idx], 0, 0);
        bp[forearmR].SetJointTargetRotation(m_WorkActions[++idx], 0, 0);
        bp[head].SetJointTargetRotation(m_WorkActions[++idx], m_WorkActions[++idx], 0);

        // 강도(0..1) + 성능 배율(피로/스턴)
        float strengthMul = m_PerformanceMultiplier * (IsStunned() ? 0.45f : 1.0f);

        bp[chest].SetJointStrength(strengthMul * Mathf.Clamp01(m_WorkActions[++idx]));
        bp[spine].SetJointStrength(strengthMul * Mathf.Clamp01(m_WorkActions[++idx]));
        bp[head].SetJointStrength(strengthMul * Mathf.Clamp01(m_WorkActions[++idx]));
        bp[thighL].SetJointStrength(strengthMul * Mathf.Clamp01(m_WorkActions[++idx]));
        bp[shinL].SetJointStrength(strengthMul * Mathf.Clamp01(m_WorkActions[++idx]));
        bp[footL].SetJointStrength(strengthMul * Mathf.Clamp01(m_WorkActions[++idx]));
        bp[thighR].SetJointStrength(strengthMul * Mathf.Clamp01(m_WorkActions[++idx]));
        bp[shinR].SetJointStrength(strengthMul * Mathf.Clamp01(m_WorkActions[++idx]));
        bp[footR].SetJointStrength(strengthMul * Mathf.Clamp01(m_WorkActions[++idx]));
        bp[armL].SetJointStrength(strengthMul * Mathf.Clamp01(m_WorkActions[++idx]));
        bp[forearmL].SetJointStrength(strengthMul * Mathf.Clamp01(m_WorkActions[++idx]));
        bp[armR].SetJointStrength(strengthMul * Mathf.Clamp01(m_WorkActions[++idx]));
        bp[forearmR].SetJointStrength(strengthMul * Mathf.Clamp01(m_WorkActions[++idx]));
    }

    // ======= Hit processing (called by FistContact) =======
    public void ReceivePunch(
        BoxerAgent attacker,
        FistContact attackerFist,
        Collider victimCollider,
        float hitForce,
        Vector3 contactPoint,
        Vector3 contactNormal,
        Vector3 relativeVelocity,
        Vector3 fistVelocity
    )
    {
        if (m_MatchOver) return;
        if (attacker == null || attacker == this) return;
        if (attackerFist == null || victimCollider == null) return;

        if (hitForce < m_MinScoringForce) return;

        // 공격자 전역 득점 쿨다운(문지르기 방지)
        if (!attacker.TryConsumeAttackerCooldown(m_AttackerGlobalHitCooldown)) return;

        // 클린 펀치 게이트: 접근 속도 + 방향 정렬
        Vector3 toContact = contactPoint - attackerFist.transform.position;
        float toMag = toContact.magnitude;
        if (toMag < 1e-4f) return;

        Vector3 toDir = toContact / toMag;
        float approachSpeed = Vector3.Dot(fistVelocity, toDir);
        if (approachSpeed < m_MinApproachSpeed) return;

        float gloveCos = Vector3.Dot(attackerFist.transform.forward.normalized, toDir);
        float gloveFactor = Mathf.Clamp01((gloveCos - m_MinGloveDirectionCos) / (1f - m_MinGloveDirectionCos));

        float force01 = Mathf.Clamp01(Mathf.InverseLerp(m_MinScoringForce, m_MaxScoringForce, hitForce));
        float speedFactor = Mathf.Clamp01((approachSpeed - m_MinApproachSpeed) / (m_MinApproachSpeed * 2f));
        float cleanFactor = Mathf.Clamp01(0.35f + 0.65f * speedFactor) * Mathf.Clamp01(0.35f + 0.65f * gloveFactor);

        bool isHead = victimCollider.CompareTag(TagHead);
        bool isTorso = victimCollider.CompareTag(TagTorso);
        bool isGuard = victimCollider.CompareTag(TagGuard);
        bool isExplicitIllegal = victimCollider.CompareTag(TagIllegal);

        bool isScoringTarget = isHead || isTorso;

        // 반칙(간단화): 로우블로우/뒤통수/명시적 Illegal
        bool isLowBlow = contactPoint.y < (hips.position.y + m_BeltLineYOffset);
        bool isBehind = isScoringTarget && IsBehindContact(contactPoint);
        bool isFoul = isExplicitIllegal || isLowBlow || isBehind;

        if (isFoul)
        {
            attacker.AddReward(m_PenaltyFoul * (0.7f + 0.6f * force01));
            attacker.m_FoulCount++;

            if (attacker.m_FoulCount >= m_MaxFoulsBeforeDQ)
            {
                EndMatch(this, attacker, "DQ (Fouls)");
            }
            return;
        }

        if (isGuard)
        {
            attacker.AddReward(m_PenaltyBlockedToAttacker * (0.5f + 0.5f * force01));
            AddReward(m_RewardBlockedToDefender * (0.5f + 0.5f * force01));
            return;
        }

        if (!isScoringTarget) return;

        float baseReward = isHead ? m_RewardHead : m_RewardTorso;
        float scoredReward = baseReward * cleanFactor * (0.45f + 0.55f * force01);

        attacker.AddReward(scoredReward);
        AddReward(-scoredReward * m_VictimPenaltyFactor);

        attacker.m_Score += Mathf.Max(0f, scoredReward);

        float dmgMul = isHead ? m_DamageHeadMultiplier : m_DamageTorsoMultiplier;
        float damage = (10f * dmgMul) * cleanFactor * (0.35f + 0.65f * force01);
        ApplyDamage(damage);

        if (isHead && force01 >= m_StunHeadForceThreshold01 && cleanFactor >= 0.70f)
        {
            TriggerStun();
        }

        if (m_Health <= m_KOHealthThreshold)
        {
            EndMatch(attacker, this, "KO (Health)");
        }
    }

    public void RegisterEnvironmentStrike(float hitForce, Collider envCollider)
    {
        if (m_MatchOver) return;

        float force01 = Mathf.Clamp01(Mathf.InverseLerp(m_MinScoringForce, m_MaxScoringForce, hitForce));
        AddReward(m_PenaltyEnvironmentStrike * (0.6f + 0.8f * force01));

        ApplyDamage(1.5f * (0.5f + 0.5f * force01));
    }

    // ======= Cast force calibration API =======
    public void UpdateCastForceCalibration(float realForce, float approxForce)
    {
        if (approxForce <= 1e-4f || realForce <= 1e-4f) return;

        float ratio = realForce / approxForce;
        ratio = Mathf.Clamp(ratio, m_CastForceScaleClampMin, m_CastForceScaleClampMax);
        m_CastForceScale = Mathf.Lerp(m_CastForceScale, ratio, Mathf.Clamp01(m_CastForceScaleEmaAlpha));
    }

    public float GetCastForceScale() => m_CastForceScale;

    // ======= Match end (Safe) =======
    private void EndMatch(BoxerAgent winner, BoxerAgent loser, string reason)
    {
        if (winner == null || loser == null) return;
        if (winner.m_MatchOver || loser.m_MatchOver) return;

        winner.m_MatchOver = true;
        loser.m_MatchOver = true;

        winner.AddReward(m_DecisionWinReward);
        loser.AddReward(m_DecisionLoseReward);

        float diff = winner.m_Score - loser.m_Score;
        float diffBonus = Mathf.Clamp(diff, -10f, 10f) * 0.05f;
        winner.AddReward(Mathf.Max(0f, diffBonus));
        loser.AddReward(-Mathf.Max(0f, diffBonus));

        // (중요) 여기서 즉시 EndEpisode() 호출하지 않고 “요청”만 건다
        winner.RequestEndEpisode();
        loser.RequestEndEpisode();
    }

    // ======= Helpers =======
    private bool TryConsumeAttackerCooldown(float cooldown)
    {
        if (Time.time - m_LastScoringTime < cooldown) return false;
        m_LastScoringTime = Time.time;
        return true;
    }

    private bool IsBehindContact(Vector3 contactPoint)
    {
        Vector3 fwd = hips.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) return false;
        fwd.Normalize();

        Vector3 dir = contactPoint - hips.position; dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f) return false;
        dir.Normalize();

        return Vector3.Dot(fwd, dir) < 0f;
    }

    private void ApplyDamage(float dmg)
    {
        m_Health = Mathf.Max(0f, m_Health - Mathf.Max(0f, dmg));
    }

    private bool IsStunned() => Time.time < m_StunUntilTime;

    private void TriggerStun()
    {
        float until = Time.time + m_StunSeconds;
        if (until > m_StunUntilTime) m_StunUntilTime = until;
    }

    private void UpdateOrientationObjects()
    {
        if (m_OrientationCube == null || targetTransform == null) return;
        m_OrientationCube.UpdateOrientation(hips, targetTransform);
    }

    private void SetRootYaw(float yawDeg)
    {
        transform.rotation = Quaternion.Euler(0f, yawDeg, 0f);
    }

    private void AddRingObservations(VectorSensor sensor)
    {
        if (ringBounds == null) return;

        Vector3 localPos = ringBounds.transform.InverseTransformPoint(hips.position);
        Vector3 c = ringBounds.center;
        Vector3 half = ringBounds.size * 0.5f;

        Vector3 toCenterLocal = (c - localPos);
        sensor.AddObservation(toCenterLocal.x);
        sensor.AddObservation(toCenterLocal.z);

        float distToPosX = (c.x + half.x) - localPos.x;
        float distToNegX = localPos.x - (c.x - half.x);
        float distToPosZ = (c.z + half.z) - localPos.z;
        float distToNegZ = localPos.z - (c.z - half.z);

        float normX = Mathf.Max(half.x, 1e-3f);
        float normZ = Mathf.Max(half.z, 1e-3f);

        float dpx = distToPosX / normX;
        float dnx = distToNegX / normX;
        float dpz = distToPosZ / normZ;
        float dnz = distToNegZ / normZ;

        sensor.AddObservation(dpx);
        sensor.AddObservation(dnx);
        sensor.AddObservation(dpz);
        sensor.AddObservation(dnz);

        float minD = Mathf.Min(dpx, dnx, dpz, dnz);
        sensor.AddObservation(minD);
    }

    private bool IsOutOfRing()
    {
        if (ringBounds == null) return false;

        Vector3 localPos = ringBounds.transform.InverseTransformPoint(hips.position);
        Vector3 c = ringBounds.center;
        Vector3 half = ringBounds.size * 0.5f;

        bool outX = (localPos.x < (c.x - half.x - m_RingOutMargin)) || (localPos.x > (c.x + half.x + m_RingOutMargin));
        bool outZ = (localPos.z < (c.z - half.z - m_RingOutMargin)) || (localPos.z > (c.z + half.z + m_RingOutMargin));
        return outX || outZ;
    }

    private float GetMinBoundaryDistance01()
    {
        if (ringBounds == null) return 1f;

        Vector3 localPos = ringBounds.transform.InverseTransformPoint(hips.position);
        Vector3 c = ringBounds.center;
        Vector3 half = ringBounds.size * 0.5f;

        float distToPosX = (c.x + half.x) - localPos.x;
        float distToNegX = localPos.x - (c.x - half.x);
        float distToPosZ = (c.z + half.z) - localPos.z;
        float distToNegZ = localPos.z - (c.z - half.z);

        float normX = Mathf.Max(half.x, 1e-3f);
        float normZ = Mathf.Max(half.z, 1e-3f);

        return Mathf.Min(distToPosX / normX, distToNegX / normX, distToPosZ / normZ, distToNegZ / normZ);
    }

    private Vector3 GetAvgVelocity()
    {
        Vector3 sum = Vector3.zero;
        int n = 0;
        foreach (var item in m_JdController.bodyPartsList)
        {
            n++;
            sum += item.rb.linearVelocity;
        }
        return sum / Mathf.Max(1, n);
    }

    // ======= CoM / Stability =======
    private Vector3 ComputeCenterOfMassWorld()
    {
        float totalMass = 0f;
        Vector3 sum = Vector3.zero;

        foreach (var bp in m_JdController.bodyPartsList)
        {
            float m = Mathf.Max(0.0001f, bp.rb.mass);
            totalMass += m;
            sum += bp.rb.worldCenterOfMass * m;
        }

        return totalMass > 1e-6f ? (sum / totalMass) : hips.position;
    }

    private Vector3 ComputeCenterOfMassVelocityWorld()
    {
        float totalMass = 0f;
        Vector3 sum = Vector3.zero;

        foreach (var bp in m_JdController.bodyPartsList)
        {
            float m = Mathf.Max(0.0001f, bp.rb.mass);
            totalMass += m;
            sum += bp.rb.linearVelocity * m;
        }

        return totalMass > 1e-6f ? (sum / totalMass) : Vector3.zero;
    }

    private void ComputeStability2D(out float stability01, out float signedDistanceToSupport, out Vector2 outwardDir)
    {
        var dict = m_JdController.bodyPartsDict;
        BodyPart footLBP = dict[footL];
        BodyPart footRBP = dict[footR];

        bool L = footLBP.groundContact.touchingGround;
        bool R = footRBP.groundContact.touchingGround;

        Vector3 com = ComputeCenterOfMassWorld();
        Vector2 com2 = new Vector2(com.x, com.z);

        outwardDir = Vector2.zero;

        if (!L && !R)
        {
            stability01 = 0f;
            signedDistanceToSupport = -1f;
            outwardDir = Vector2.zero;
            return;
        }

        Vector2 pL = new Vector2(footLBP.rb.position.x, footLBP.rb.position.z);
        Vector2 pR = new Vector2(footRBP.rb.position.x, footRBP.rb.position.z);
        float r = Mathf.Max(0.01f, m_SupportFootRadius);

        if (L && R)
        {
            float distToSeg = DistancePointToSegment(com2, pL, pR, out Vector2 closest);
            signedDistanceToSupport = r - distToSeg;
            stability01 = Mathf.Clamp01((signedDistanceToSupport + r) / (2f * r));

            Vector2 v = (com2 - closest);
            outwardDir = v.sqrMagnitude > 1e-8f ? v.normalized : Vector2.zero;
            return;
        }

        Vector2 p = L ? pL : pR;
        float dist = Vector2.Distance(com2, p);
        signedDistanceToSupport = r - dist;
        stability01 = Mathf.Clamp01((signedDistanceToSupport + r) / (2f * r));

        Vector2 vv = (com2 - p);
        outwardDir = vv.sqrMagnitude > 1e-8f ? vv.normalized : Vector2.zero;
    }

    private float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b, out Vector2 closest)
    {
        Vector2 ab = b - a;
        float ab2 = ab.sqrMagnitude;

        if (ab2 < 1e-8f)
        {
            closest = a;
            return Vector2.Distance(p, a);
        }

        float t = Vector2.Dot(p - a, ab) / ab2;
        t = Mathf.Clamp01(t);
        closest = a + t * ab;
        return Vector2.Distance(p, closest);
    }

    // ======= Fatigue / Non-stationarity control =======
    private void UpdateFatigueAndPerformance()
    {
        float energy = 0f;
        foreach (var bp in m_JdController.bodyPartsList)
        {
            float strengthNorm = Mathf.Clamp01(bp.currentStrength / Mathf.Max(m_JdController.maxJointForceLimit, 1e-3f));
            float w2 = bp.rb.angularVelocity.sqrMagnitude;
            energy += strengthNorm * w2;
        }

        float drain = m_StaminaDrainScale * energy * Time.fixedDeltaTime;
        float recover = m_StaminaRecoveryPerSecond * Time.fixedDeltaTime;

        m_Stamina01 = Mathf.Clamp01(m_Stamina01 - drain + recover);

        float health01 = Mathf.Clamp01(m_Health / Mathf.Max(1f, m_MaxHealth));
        float perfTarget = health01 * (0.25f + 0.75f * m_Stamina01);

        if (IsStunned()) perfTarget *= 0.45f;

        perfTarget = Mathf.Max(m_MinPerformanceMultiplier, perfTarget);
        m_PerformanceMultiplier = perfTarget;

        float desiredMax = m_BaseMaxJointForceLimit * perfTarget;
        m_JdController.maxJointForceLimit = Mathf.Lerp(
            m_JdController.maxJointForceLimit,
            desiredMax,
            Mathf.Clamp01(m_MaxForceLimitSmooth)
        );
    }

    // ======= Action smoothing helpers =======
    private float GetAnnealedAlpha()
    {
        float t = m_SmoothingAnnealEpisodes > 0
            ? Mathf.Clamp01((float)m_EpisodeCount / m_SmoothingAnnealEpisodes)
            : 1f;

        return Mathf.Lerp(m_SmoothingStartAlpha, m_SmoothingEndAlpha, t);
    }

    private float GetAnnealedMaxDelta()
    {
        float t = m_SmoothingAnnealEpisodes > 0
            ? Mathf.Clamp01((float)m_EpisodeCount / m_SmoothingAnnealEpisodes)
            : 1f;

        return Mathf.Lerp(m_MaxDeltaStart, m_MaxDeltaEnd, t);
    }

    private void EnsureActionBufferSize(int actionSize)
    {
        if (m_LastRawActions == null || m_LastRawActions.Length != actionSize)
        {
            m_LastRawActions = new float[actionSize];
            m_SmoothedActions = new float[actionSize];
            m_WorkActions = new float[actionSize];
        }
    }

    private void ResetActionBuffers()
    {
        for (int i = 0; i < m_LastRawActions.Length; i++)
        {
            m_LastRawActions[i] = 0f;
            m_SmoothedActions[i] = 0f;
            m_WorkActions[i] = 0f;
        }
    }

    private float ComputeActionDeltaPenalty(ActionSegment<float> raw)
    {
        float sumSq = 0f;
        for (int i = 0; i < raw.Length; i++)
        {
            float d = raw[i] - m_LastRawActions[i];
            sumSq += d * d;
            m_LastRawActions[i] = raw[i];
        }
        return -m_ActionDeltaPenaltyScale * sumSq;
    }

    private float ComputeAngularVelocityPenalty()
    {
        float sum = 0f;
        foreach (var bp in m_JdController.bodyPartsList)
        {
            sum += bp.rb.angularVelocity.sqrMagnitude;
        }
        return -m_JointAngularVelocityPenaltyScale * sum;
    }

    // ======= Reset safety helpers =======
    private void SetAllBodiesKinematic(bool isKinematic)
    {
        if (m_AllRigidbodies == null) return;
        foreach (var rb in m_AllRigidbodies) rb.isKinematic = isKinematic;
    }

    private void ZeroAllBodyVelocities()
    {
        if (m_AllRigidbodies == null) return;
        foreach (var rb in m_AllRigidbodies)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }

    private void SleepAllBodies()
    {
        if (m_AllRigidbodies == null) return;
        foreach (var rb in m_AllRigidbodies) rb.Sleep();
    }

    private void ApplyRigidBodySafetyLimits()
    {
        if (m_AllRigidbodies == null) return;
        foreach (var rb in m_AllRigidbodies)
        {
            rb.maxAngularVelocity = m_MaxAngularVelocityClamp;
            rb.angularDamping = m_AngularDrag;
        }
    }
}
