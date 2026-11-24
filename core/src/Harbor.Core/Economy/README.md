# Economy

서버 권위 경제. 모든 돈 이동은 `Ledger`의 복식부기 전표로만 일어난다 (ADR 0005).

- `Money.cs` — 통화(Dirty/Clean), 계정 ID(지갑·은행·스태시·크루·비즈니스·faucet·sink), 전표.
- `Ledger.cs` — `Post`/`PostBatch`(원자적, TxId 멱등). 잔고 음수 불가(faucet 제외).
- `EconomySystem.cs` — 상점, 세탁(즉시/비즈니스 대기열), 스태시, 크루 금고, 사망 드롭, 페널티.
- `PayoutGovernor.cs` — 같은 활동 반복 보상 체감.
- 튜닝: `data/economy/*.json`, `data/world/safehouses.json`.

## 확장 포인트
- 새 수입원: `Payout(profile, currency, base, activity, txId)` 호출. faucet 이름 = 활동 id.
- 새 지출: `AccountId.Sink("<이유>")`로 전표를 만들고 `Ledger.PostBatch`.
- 네트워크를 모른다. 클라이언트 요청 해석은 `Server/GameServer.cs`.
