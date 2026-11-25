# Server

`GameServer` = 컴포지션 루트. Net과 규칙 모듈(Combat·Economy·Activities)을 아는 유일한 계층.

- 접속 → 프로필/액터 생성, 세이프하우스 스폰, 잔고 전송. 이탈 → 로그아웃 세금.
- `Requests.cs` — 클라 요청 타입과 코덱. 모든 요청은 nonce를 가지며 TxId로 변환돼 중복 과금 방지.
- `WireEvents.cs` — 서버→클라 이벤트 id와 인코더.
- 규칙 이벤트(EventBus) → 관련 클라에 와이어 이벤트 릴레이.

`HarborAuthority`는 항만 플레이테스트의 로컬 및 전용 서버에서 같은 규칙을 사용한다.
`game/data/missions/harbor_jobs.json`에 의뢰 위치, 단계, 제한 시간, 보상을 정의한다. 서버가
운전자와 참가자 자격을 확인하고, 전투·회수·체크포인트·주차를 검증한 뒤 참가자별 보상을
원장에 기록한다. 잔액은 현재 연결 세션에 한해 표시한다. `HarborState`는 의뢰, 교통, 보행자
상태를 전송한다. 현재 항만 클라이언트와 서버는 프로토콜 2를 사용한다.

## 확장 포인트
- 새 요청: `RequestType`에 상수 추가 → `GameServer` dispatch, 또는 `ExtraRequest` 훅.
- 잘못된 페이로드는 거부만 하고 서버는 계속 돈다(테스트로 보장).
