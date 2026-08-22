# 저장소 구조 / 작업 방식

## 리모트
- origin: `saewoo-zip/<repo>`. 용도: 작업 브랜치.
- upstream: `<원작자>/<repo>`. 용도: pull. push: 없음.
- publish: `saewoo-dalamud/<repo>`. 용도: org 배포. push 대상: 항상 이 리모트. 트리거: org 시크릿 사용 Actions.

## 브랜치
- master: upstream 미러. 갱신 방식: fast-forward. 개인 커밋: 없음.
- main: 분기 기준: master. 내용: 개인 커스텀 전체. publish push 대상: main.
- docs: 용도: 코드 무관 문서(조사 기록 등). main/master 포함 여부: 없음.

## AGENTS.md / WORKFLOW.md 동기화
- 소스: `saewoo-dalamud/.github`
- sync 대상: `saewoo-dalamud/<repo>`(org)
- 설정 파일: `.github/sync.yml`

## 새 저장소 셋업
```bash
git clone <upstream repo>
git remote rename origin upstream
git remote add origin https://github.com/saewoo-zip/<repo>.git
git remote add publish https://github.com/saewoo-dalamud/<repo>.git
git checkout -b main
```

## 버저닝 (AssemblyVersion)
- InternalName: 원본과 동일
- 버전 번호: 원본과 겹침 없음 (InternalName 동일로 인한 제약)
- 구성요소 범위: 4자리, 각 0~65535 (CLR 제약)
- Major.Minor.Build: 원본 값과 동일
- Revision 리셋 조건: 원본 Major.Minor.Build 변경 시점 → 값 10000
- Revision 증가: 그 외 모든 릴리즈(개인 패치, 원본 Revision 흡수 merge 포함) → +1
- Revision 값: 항상 직전 값보다 큼. 중간 재리셋 없음.
- 원본 기준 버전 기록 위치: 저장소별 `UPSTREAM_VERSION` 파일
- 2자리 버전(Major.Build) 원본: 동일 규칙 적용, 마지막 자리만 10000 리셋

예 (원본 `1.3.0.2` 기준):
- 첫 릴리즈: `1.3.0.10000`
- 개인 패치: `1.3.0.10001`
- 원본 `1.3.0.9` 흡수: `1.3.0.10002`
- 원본 `1.3.1.0`(Build 변경) 흡수: `1.3.1.10000`

## 릴리즈
- 릴리즈 생성 전: changelog 내용 사용자에게 확인. 확인 없이 자동 생성 문구로 진행 금지.
- 확인 절차: 커밋 로그 기반 변경 요약을 사용자에게 제시 → 사용자 승인/수정 → 확정된 내용으로 릴리즈 생성.
- `.github/workflows/release.yml`: 소스는 `saewoo-dalamud/.github` (동기화 대상, 저장소별 수정 금지).
- 워크플로 실행 시 `changelog` input에 확인된 텍스트 입력. 생성되는 릴리즈 노트 = `changelog` input + upstream 기준 버전 + GitHub 자동 생성 전체 변경목록(항상 포함).
- 저장소별 필수 repo variable: `PROJECT_NAME` = 프로젝트 폴더명(= csproj 파일명, `<프로젝트폴더>/<프로젝트폴더>.csproj` 구조 전제).
- 저장소별 필수 파일: `UPSTREAM_VERSION` (없으면 릴리즈 노트 생성 단계 실패).

## MyDalamudPlugins 등록
`plugins.json` 추가 항목:
```json
{
  "repo": "saewoo-dalamud/<repo>",
  "manifestPath": "<프로젝트폴더>/<InternalName>.json",
  "iconPath": "<프로젝트폴더>/images/icon.png"
}
```
- manifestPath 매니페스트의 InternalName = 릴리즈 zip 내 매니페스트의 InternalName (일치 필수)
- InternalName 변경 후 조치: 릴리즈 재생성
