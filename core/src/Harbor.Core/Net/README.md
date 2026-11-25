# Net

서버 권한형 멀티플레이 (ADR 0004). 엔진 없이 동작 — 전용 서버와 테스트가 그대로 쓴다.

- `ServerSession` (ISimSystem): 접속·입력 버퍼·이동 적용·랙 보정 기록·관심 영역 스냅샷. 게임 규칙에는 이벤트로 넘긴다:
  `PlayerJoined`, `PlayerLeft`, `PlayerInputApplied`(사격·상호작용 처리용, 되감기 틱 포함), `ClientRequest`(구매 등).
- `ClientSession`: 입력 → 예측(`Sim/Movement`) → 전송, 스냅샷 → 보정(미확인 입력 재적용), 원격 엔티티 보간.
- `Messages`, `NetWriter/NetReader`, `Quant`: 이진 포맷. 메시지 추가 = `MsgType` + 인코더/디코더를 여기에.
- `InMemoryNetwork`: 지연·지터·손실 시뮬레이터 (테스트, 로컬 플레이).
- `TcpTransport`: LAN 플레이테스트용 TCP 구현 (ADR 0007). 호스트와 클라이언트가 업데이트마다 `Pump()`를 호출하며 전송 큐와 프레임 크기를 제한한다.
- `LagCompHistory`, `SnapshotInterpolator`.

확장 포인트: 새 게임 이벤트는 `ServerSession.SendEvent/Broadcast(type, payload)` — 타입 번호는 `NetEventTypes`에 모은다.
규칙: 클라이언트가 보낸 값은 위치·피해·돈이 아니다. 디코더는 개수·길이 상한을 반드시 검사.
다음: 스냅샷 델타 압축(현재 16명 근접 시 ~50kbps).
