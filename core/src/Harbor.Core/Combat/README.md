# Combat

서버 권한 전투 규칙 (기획: `docs/CONTENT_DESIGN.md` §4). 엔진·네트워크를 모른다 — 입력은 `CombatInput`, 결과는 이벤트.

- `CombatSystem`: 발사(연사·반자동·펌프, 틱당 다발), 재장전, 무기 교체, 랙 보정 포즈(`LagCompHistory`)로 히트 판정, 데미지·방어구, 다운/부활/출혈사, 재생, 리스폰, 폭발물 연쇄, 제압, 반복 킬 감지.
- `Ballistics` (히트스캔 + 재질 관통), `Hitboxes` (머리 구·몸통/다리 캡슐, 앉기 포즈), `DamageModel` (감쇠·부위·방어구, 순수 함수).
- 데이터: `data/weapons/*.json`, `data/combat/combat.json`, `data/combat/materials.json`.
- 이벤트: `ShotFired`(소음→AI·수배), `ActorDamaged`, `ActorDowned`, `ActorKilled`, `ActorRevived`, `ActorRespawned`, `ExplosiveDetonated`, `RepeatKillDetected`.

확장 포인트: `SpawnPointFor`(은신처 리스폰), `UsesDownedState`(모드별 다운 규칙), `ApplyDamage`(근접·스크립트 피해).
결정성: 탄퍼짐 RNG는 (사수, 틱, 펠릿) 해시 → 같은 입력이면 같은 결과.
