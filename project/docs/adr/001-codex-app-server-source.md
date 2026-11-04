# ADR 001: Codex App Serverを唯一のデータソースとする

- Status: Accepted
- Date: 2026-07-22

公式のstable `codex app-server` RPCを利用する。認証ファイル、session JSONL、ブラウザーcookie、非公開ChatGPT endpointは利用しない。

## 作業状態との境界（2026-11-03）

本ADRの唯一のデータソースという決定は利用枠取得に適用する。作業状態は[ADR 012](012-work-status-hooks.md)の公式hooksで取得し、認証情報・session JSONL・非公開endpointを利用しない規則は維持する。
