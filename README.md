# Payment Platform Patterns — C# .NET Core

> Real-world design patterns and SOLID principles extracted from a production financial transaction platform processing 30+ external payment channels with sub-second response times.

## Why This Repo Exists

Most "design pattern" repos show toy examples. This one doesn't. Every pattern here comes from a live payment platform that handles:

- **30+ integrated payment channels** (PhonePe, Paytm, FreeCharge, Airtel, MobiKwik...)
- **Multi-currency fund management** with real-time balance tracking
- **Atomic ledger updates** with idempotent transaction processing
- **Event-driven architecture** with pub/sub notifications

The patterns here come from a live payment platform handling 30+ external channels with sub-second transaction processing. This repo presents them in **idiomatic C# .NET Core** with runnable code examples.

> Every pattern in this repo was extracted from a real payment system. No toy examples — each one solves a problem that actually occurred in production.

## What's Inside

### 01 — SOLID Principles

| Principle | Pattern | Real-World Example |
|-----------|---------|-------------------|
| [SRP](01-solid/srp.md) | Single Responsibility | `BalanceService` handles ONLY fund changes; reconciliation is delegated |
| [OCP](01-solid/ocp.md) | Open/Closed | Payment signing engine: add new algorithms without touching core |
| [LSP](01-solid/lsp.md) | Liskov Substitution | All payment handlers inherit `BaseHandler` — any can replace any |
| [ISP](01-solid/isp.md) | Interface Segregation | Split `IPaymentChannel` into `ICollection`, `IDisbursement`, `IStatusQuery` |
| [DIP](01-solid/dip.md) | Dependency Inversion | Services depend on `ILogger`, `IDistributedCache` — not concrete implementations |

### 02 — Design Patterns

| Pattern | Where | Problem It Solved |
|---------|-------|-------------------|
| [Strategy](02-design-patterns/strategy.md) | Multi-algorithm signing engine | 30+ channels, each with a different signature algorithm |
| [Template Method](02-design-patterns/template-method.md) | `BaseHandler.PrepareAsync()` lifecycle | Every request needs XSS check → IP validation → param check — but in that exact order |
| [Observer](02-design-patterns/observer.md) | Event-driven pub/sub via Redis | Order status change → notify merchant + update dashboard + trigger reconciliation |
| [Chain of Responsibility](02-design-patterns/chain-of-responsibility.md) | ASP.NET middleware pipeline | Request flows through XSS filter → IP blacklist → auth → rate limit → controller |
| [Factory](02-design-patterns/factory.md) | Payment channel factory | 30+ channel types, instantiated from config at runtime |
| [Decorator](02-design-patterns/decorator.md) | Encryption interceptor | Wraps HTTP calls with automatic encrypt/decrypt |

### 03 — Architecture

| Topic | Description |
|-------|-------------|
| [Service Layer](03-architecture/service-layer.md) | Why extract business logic from controllers |
| [Post-Commit Hook](03-architecture/post-commit-hook.md) | Transaction-aware callbacks without coupling |
| [Multi-Channel Gateway](03-architecture/multi-channel-gateway.md) | Normalizing 30+ external webhooks into one internal event format |

### Code Snippets

Browse [`code-snippets/`](code-snippets/) for runnable C# examples of each pattern.

---

## How to Learn From This Repo

Start with the patterns that match what you're building:

1. **Building APIs?** → [Chain of Responsibility](02-design-patterns/chain-of-responsibility.md) + [Template Method](02-design-patterns/template-method.md)
2. **Integrating 3rd-party services?** → [Strategy](02-design-patterns/strategy.md) + [Factory](02-design-patterns/factory.md) + [Multi-Channel Gateway](03-architecture/multi-channel-gateway.md)
3. **Managing transactions?** → [Service Layer](03-architecture/service-layer.md) + [Post-Commit Hook](03-architecture/post-commit-hook.md)
4. **Decoupling systems?** → [Observer](02-design-patterns/observer.md) + [DIP](01-solid/dip.md)

Each doc follows the same structure:
1. **Real Scenario** — the problem as it existed in production
2. **The Pattern** — how it was applied
3. **C# Implementation** — idiomatic .NET code
4. **Trade-offs** — what we gained and what we sacrificed

---

## Author

Ray Li — Senior Software Engineer, 15+ years building financial transaction systems.  
AWS Certified DevOps Engineer – Professional.
