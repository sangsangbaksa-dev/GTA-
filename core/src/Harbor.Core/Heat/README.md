# Heat

액터별 수배 레벨(0~5). 테이블 `data/ai/heat.json`.

- `HeatSystem.Report(actor, crime)` — 범죄 열기 추가. 같은 범죄는 `repeatCrimeSeconds` 안에 한 번만 쌓인다.
- 마지막 범죄 후 `decayDelaySeconds`가 지나야 감쇠 시작. 레벨 변화 시 `WantedLevelChanged`.
- `AddLevels`(괴롭힘 처벌), `Clear`(사망·접속 종료).
- 범죄 보고는 서버 조립(`GameServer`: 발사·살인)과 미션 목표(`crime` 필드)가 한다. Heat는 Combat를 모른다.

## 확장 포인트
- 경찰 출동(Agents)은 `LevelOf`와 `Levels[n].Units/ResponseSeconds`를 읽어 구현 (M4).
- 새 범죄: `crimeHeat`에 키 추가 → 코드 변경 없음.
