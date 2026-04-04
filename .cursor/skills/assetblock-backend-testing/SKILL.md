---
name: assetblock-backend-testing
description: >-
  Unit testing patterns for AssetBlock.Application.Tests: xUnit, NSubstitute
  (not Moq), FluentAssertions, handler and FluentValidation tests. Use when
  writing or reviewing tests in asblock-backend, mocking I*Store services, or
  asserting Ardalis.Result outcomes.
---

# AssetBlock.Application.Tests conventions

This project uses **xUnit**, **NSubstitute** for mocks, and **FluentAssertions** for assertions. It does **not** use Moq.

## Project setup

- Test project: `AssetBlock.Application.Tests` targets `net10.0`, references `AssetBlock.Application` and `AssetBlock.Infrastructure` (when needed).
- Packages: `xunit`, `FluentAssertions`, `NSubstitute`, `Microsoft.NET.Test.Sdk`, `coverlet.collector` / `coverlet.msbuild`; `Microsoft.EntityFrameworkCore.InMemory` is referenced for tests that need it.
- Global `using Xunit` is enabled in the csproj.

## Layout and naming

- Mirror application structure: `UseCases/<Area>/<HandlerName>Tests.cs`, `Validators/<Name>Tests.cs`.
- Test method names: `Handle_When<Condition>_Should<Expected>` or `Validate_When<Condition>_Should<Expected>`.

## Handler tests — NSubstitute

- Create fakes: `Substitute.For<IUserStore>()`, etc.
- Stub async methods: `_store.GetById(id, Arg.Any<CancellationToken>()).Returns(entity);`
- Verify calls: `await _store.Received(1).Save(entity, Arg.Any<CancellationToken>());` and `await _store.DidNotReceive().Delete(Arg.Any<Guid>());`
- Use `NullLogger<T>.Instance` from `Microsoft.Extensions.Logging.Abstractions` when the handler requires `ILogger<T>`.

## Asserting Ardalis.Result

- Success: `result.IsSuccess.Should().BeTrue();` then `result.Value`.
- **ResultError / invalid-style codes** (e.g. login invalid credentials): `result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_…);`
- **NotFound / Forbidden / Conflict**: `result.Status.Should().Be(ResultStatus.NotFound);` (or `Forbidden`, `Conflict`) and `result.Errors.Should().Contain(ErrorCodes.ERR_…);`

Check existing handlers in the same feature to see whether the handler uses `ResultError` (Invalid + validation errors) vs `Result.NotFound` (errors collection).

## FluentValidation tests

- Instantiate the validator directly: `private readonly XxxValidator _validator = new();`
- Call `await _validator.ValidateAsync(command);`
- Assert `result.IsValid`, `result.Errors` property names and messages.
- Use `[Theory]` + `[InlineData]` for multiple invalid inputs.

## What to cover

- Happy path, missing entity (NotFound), authorization (Forbidden), conflicts, and validation failures.
- When the handler publishes events or invalidates cache, assert `Received` / `DidNotReceive` on those dependencies.

## Commands

From `asblock-backend`:

```bash
dotnet test AssetBlock.Application.Tests/AssetBlock.Application.Tests.csproj
```

## Related

- Backend conventions: [../assetblock-backend/SKILL.md](../assetblock-backend/SKILL.md)
