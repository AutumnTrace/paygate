# 💳 paygate

**Payment orchestration gateway for iGaming operators: routes deposits and payouts across PSPs with health-aware failover, Polly circuit breakers, idempotent REST APIs and signed webhooks.**

[![CI](https://github.com/AutumnTrace/paygate/actions/workflows/ci.yml/badge.svg)](https://github.com/AutumnTrace/paygate/actions/workflows/ci.yml)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> [!NOTE]
> Work in progress. The domain core (payment state machine, money value objects, domain events) is in place; routing, the REST API, idempotency and webhooks land next.

## Build and test

```bash
dotnet build
dotnet test --filter "Category!=Integration"
```

## License

[MIT](LICENSE) © Mina Rotari
