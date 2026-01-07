// GroundContact.cs
// 목적:
// 1) 바닥에 "순간" 닿는 건 무시 (탐색/공격 시도 죽이지 않기)
// 2) 일정 시간 이상 "지속 접촉"이면 누워있는 상태로 보고, 그때부터 초당 감점(지속 감점)
// 3) 더 오래 지속되면(= 사실상 행동 불능) 그때 에피소드 종료 요청
//
// 중요: EndEpisode()를 충돌 콜백에서 직접 호출하지 말고, BoxerAgent.RequestEndEpisode()로 요청만 보냄
//       (물리 스텝 중 리셋 → 스핀/폭발 원인)

using UnityEngine;
using Unity.MLAgents;

namespace Unity.MLAgentsExamples
{
    [DisallowMultipleComponent]
    public class GroundContact : MonoBehaviour
    {
        [HideInInspector] public Agent agent;

        [Header("Ground Tag")]
        public string groundTag = "ground";

        [Header("Sustain thresholds")]
        [Tooltip("이 시간 이상 '지속 접촉'이어야 누워있음으로 인정(이전까지는 무시)")]
        public float minSustainTime = 0.6f;

        [Tooltip("누워있음으로 인정된 이후, 초당 감점(음수). 0이면 감점 없음")]
        public float penaltyPerSecond = -0.15f;

        [Tooltip("이 시간 이상 지속되면 행동 불능으로 보고 에피소드 종료 요청. 0이면 종료 안 함")]
        public float endEpisodeAfter = 1.2f;

        [Header("Runtime")]
        public bool touchingGround;
        public float touchingDuration;

        private void OnCollisionEnter(Collision col)
        {
            if (!col.transform.CompareTag(groundTag)) return;

            touchingGround = true;

            // 접촉 시작: 시간 누적 시작
            // (다중 콜라이더로 Enter가 여러 번 올 수 있어도, duration이 이미 누적 중이면 그대로 둠)
            if (touchingDuration <= 0f)
                touchingDuration = 0f;
        }

        private void OnCollisionStay(Collision col)
        {
            if (!col.transform.CompareTag(groundTag)) return;
            if (agent == null) return;

            touchingGround = true;
            touchingDuration += Time.fixedDeltaTime;

            // 1) 순간 접촉은 무시
            if (touchingDuration < minSustainTime) return;

            // 2) 누워있는 동안 지속 감점 (물리 스텝 기준으로 일정하게)
            if (penaltyPerSecond != 0f)
            {
                agent.AddReward(penaltyPerSecond * Time.fixedDeltaTime);
            }

            // 3) 일정 시간 이상 누워있으면(행동 불능) 종료 요청
            if (endEpisodeAfter > 0f && touchingDuration >= endEpisodeAfter)
            {
                RequestEndEpisodeSafely(agent);
            }
        }

        private void OnCollisionExit(Collision col)
        {
            if (!col.transform.CompareTag(groundTag)) return;

            touchingGround = false;
            touchingDuration = 0f;
        }

        private void RequestEndEpisodeSafely(Agent a)
        {
            if (a == null) return;

            // BoxerAgent는 “요청”으로 보내고, 실제 EndEpisode는 FixedUpdate에서 처리하는 구조가 안전함
            if (a is BoxerAgent boxer)
            {
                boxer.RequestEndEpisode();
            }
            else
            {
                a.EndEpisode();
            }
        }
    }
}
