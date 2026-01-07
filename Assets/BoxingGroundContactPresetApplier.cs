// BoxingGroundContactPresetApplier.cs
// 목적: 사용자님이 요청한 “부위별 GroundContact 지속접촉 페널티/종료 기준”을
//       Inspector에서 일일이 입력하지 않고, 한 번에 표 값대로 세팅하는 스크립트.
// 전제: GroundContact.cs는 (최소) 아래 필드를 가지고 있어야 합니다.
//   - public string groundTag
//   - public float minSustainTime
//   - public float penaltyPerSecond
//   - public float endEpisodeAfter
//   - [HideInInspector] public Agent agent
//
// 사용법:
// 1) 이 스크립트를 에이전트 루트(BoxerAgent가 붙은 오브젝트)에 추가
// 2) boxerAgent 레퍼런스 연결
// 3) applyOnStart 체크(권장) 또는 ApplyNow()를 수동 호출
//
// 주의:
// - groundTag(기본 "ground")가 실제 바닥 Tag와 정확히 일치해야 합니다.
// - 발(foot)은 접지가 정상행동이라 종료/패널티는 0으로 유지(관측용으로만 사용)

using UnityEngine;
using Unity.MLAgents;

namespace Unity.MLAgentsExamples
{
    [DisallowMultipleComponent]
    public class BoxingGroundContactPresetApplier : MonoBehaviour
    {
        [Header("Target")]
        [SerializeField] private BoxerAgent boxerAgent;

        [Header("Ground Tag")]
        [SerializeField] private string groundTag = "ground";

        [Header("Apply")]
        [SerializeField] private bool applyOnStart = true;

        private void Reset()
        {
            boxerAgent = GetComponent<BoxerAgent>();
        }

        private void Start()
        {
            if (applyOnStart)
            {
                ApplyNow();
            }
        }

        /// <summary>
        /// 표(권장 세트) 값대로 GroundContact를 부위별로 구성합니다.
        /// - head/chest/spine/hips : 일정 시간 누워있으면 지속 감점 + 더 오래면 종료
        /// - hand/forearm          : 종료 없이 지속 감점(기어다니기 억제)
        /// - foot 및 기타 부위      : 관측용(패널티/종료 0)
        /// </summary>
        [ContextMenu("Apply GroundContact Preset Now")]
        public void ApplyNow()
        {
            if (boxerAgent == null)
            {
                Debug.LogError("[BoxingGroundContactPresetApplier] boxerAgent가 null입니다.");
                return;
            }

            // 0) 모든 부위에 GroundContact가 없으면 추가, 있으면 관측용 기본값(패널티/종료 0)으로 초기화
            ApplyObservationOnly(boxerAgent.hips);
            ApplyObservationOnly(boxerAgent.chest);
            ApplyObservationOnly(boxerAgent.spine);
            ApplyObservationOnly(boxerAgent.head);

            ApplyObservationOnly(boxerAgent.thighL);
            ApplyObservationOnly(boxerAgent.shinL);
            ApplyObservationOnly(boxerAgent.footL);

            ApplyObservationOnly(boxerAgent.thighR);
            ApplyObservationOnly(boxerAgent.shinR);
            ApplyObservationOnly(boxerAgent.footR);

            ApplyObservationOnly(boxerAgent.armL);
            ApplyObservationOnly(boxerAgent.forearmL);
            ApplyObservationOnly(boxerAgent.handL);

            ApplyObservationOnly(boxerAgent.armR);
            ApplyObservationOnly(boxerAgent.forearmR);
            ApplyObservationOnly(boxerAgent.handR);

            // 1) “진짜 다운” 판정(종료까지 가는 부위): head/chest/spine/hips
            // 표 값 그대로 적용
            ApplySustainAndEnd(boxerAgent.head, 0.25f, -0.35f, 1.00f); // head
            ApplySustainAndEnd(boxerAgent.chest, 0.35f, -0.25f, 1.20f); // chest
            ApplySustainAndEnd(boxerAgent.spine, 0.35f, -0.20f, 1.30f); // spine
            ApplySustainAndEnd(boxerAgent.hips, 0.45f, -0.15f, 1.60f); // hips

            // 2) “기어다니기 억제(종료 없음)” 부위: hand/forearm
            ApplySustainNoEnd(boxerAgent.handL, 0.70f, -0.08f);
            ApplySustainNoEnd(boxerAgent.handR, 0.70f, -0.08f);
            ApplySustainNoEnd(boxerAgent.forearmL, 0.70f, -0.10f);
            ApplySustainNoEnd(boxerAgent.forearmR, 0.70f, -0.10f);

            // 3) foot은 접지 관측은 필요하지만 패널티/종료는 절대 주지 않음(이미 ObservationOnly로 0 처리됨)
            //    여기서 명시적으로 한 번 더 고정
            ForceNoPenaltyNoEnd(boxerAgent.footL);
            ForceNoPenaltyNoEnd(boxerAgent.footR);

            Debug.Log("[BoxingGroundContactPresetApplier] GroundContact 프리셋 적용 완료");
        }

        // ===== Internal helpers =====

        private void ApplyObservationOnly(Transform part)
        {
            if (part == null) return;

            var gc = GetOrAddGroundContact(part);
            BindAgentAndTag(gc);

            // 관측용 기본: 접촉 지속시간을 기록하되, 패널티/종료는 없음
            gc.minSustainTime = 999f;     // penalty/end를 쓰지 않으므로 의미는 크지 않지만 “실수 방지”용
            gc.penaltyPerSecond = 0f;
            gc.endEpisodeAfter = 0f;
        }

        private void ApplySustainAndEnd(Transform part, float minSustainTime, float penaltyPerSecond, float endEpisodeAfter)
        {
            if (part == null) return;

            var gc = GetOrAddGroundContact(part);
            BindAgentAndTag(gc);

            gc.minSustainTime = minSustainTime;
            gc.penaltyPerSecond = penaltyPerSecond;
            gc.endEpisodeAfter = endEpisodeAfter;
        }

        private void ApplySustainNoEnd(Transform part, float minSustainTime, float penaltyPerSecond)
        {
            if (part == null) return;

            var gc = GetOrAddGroundContact(part);
            BindAgentAndTag(gc);

            gc.minSustainTime = minSustainTime;
            gc.penaltyPerSecond = penaltyPerSecond;
            gc.endEpisodeAfter = 0f; // 종료 금지
        }

        private void ForceNoPenaltyNoEnd(Transform part)
        {
            if (part == null) return;

            var gc = GetOrAddGroundContact(part);
            BindAgentAndTag(gc);

            gc.penaltyPerSecond = 0f;
            gc.endEpisodeAfter = 0f;
            gc.minSustainTime = 999f;
        }

        private GroundContact GetOrAddGroundContact(Transform part)
        {
            var gc = part.GetComponent<GroundContact>();
            if (gc == null)
            {
                gc = part.gameObject.AddComponent<GroundContact>();
            }
            return gc;
        }

        private void BindAgentAndTag(GroundContact gc)
        {
            // GroundContact는 Agent 참조가 있어야 패널티/종료 요청이 가능
            gc.agent = boxerAgent as Agent;

            // groundTag 일치 필수
            gc.groundTag = groundTag;
        }
    }
}
