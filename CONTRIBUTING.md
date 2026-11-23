# Contributing

Thanks for taking a look. This is a small, focused codebase; the rules below keep it that way.

## Getting started

```bash
dotnet build
dotnet test --filter "Category!=Integration"   # fast suite, no Docker needed
dotnet test                                     # everything, needs Docker for Testcontainers
```

## Ground rules

- **Business rules live in `Paygate.Domain`** and come with unit tests. The API layer translates HTTP to domain calls and back, nothing more.
- **Money is never a `double`.** Use `Money` (integer minor units + ISO-4217 currency).
- **Never log secrets or payment instrument data.** API keys, webhook secrets and payment tokens must not appear in logs, exceptions or test output.
- **Time comes from `TimeProvider`**, so tests can control it.
- **Integration tests** that need Docker carry `[Trait("Category", "Integration")]`.
- Commits follow [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`, `test:`, `chore:`).
- Add a line to `CHANGELOG.md` under **Unreleased** for anything user-visible.

## Architecture decisions

Significant decisions are recorded as ADRs in [`docs/adr`](docs/adr). If a change reverses or extends one, add a new ADR rather than rewriting history.
