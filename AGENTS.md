# AGENTS.md

## 문서 작성 스타일

- 규칙과 절차는 선언적 명세 형태로 작성한다.
- 각 문장은 `대상`, `조건`, `동작`, `결과` 중 필요한 요소만 포함해 짧게 작성한다.
- 작업자가 바로 참조할 수 있도록 사실과 실행 기준을 직접 기술한다.
- 가능하면 명사형, 짧은 단문, `항목: 값` 형태를 우선한다.
- 제약사항은 금지나 당부가 아니라 허용 범위와 올바른 동작을 명시한다.
- 배경 설명은 실행 기준을 이해하는 데 필요한 정보만 포함한다.

시작 전: `WORKFLOW.md` 읽기. 캐시된 요약 사용 금지, 매회 재확인.

- 버전 결정 → `WORKFLOW.md#버저닝`
- 릴리즈 실행 → `WORKFLOW.md#릴리즈`
- upstream 반영 → `WORKFLOW.md#리모트-3개`, `#브랜치-2개`
- 새 저장소 셋업 → `WORKFLOW.md#새-저장소-셋업`
- `MyDalamudPlugins` 등록 → `WORKFLOW.md#MyDalamudPlugins-등록`
- `AGENTS.md`/`WORKFLOW.md` 동기화 → `WORKFLOW.md#동기화`
