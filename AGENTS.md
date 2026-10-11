# AGENTS.md

Guidance for AI coding agents working in this repository.

## Overview

`Splunk.Extensions.Logging` is a Microsoft.Extensions.Logging provider that ships logs to Splunk HEC (net10.0). The event format follows the signalfx/splunk-otel-collector HEC exporter and OTel .NET `ILogger` conventions; the mapping is documented in `README.md` ("Event format").

## Layout

- `src/Splunk.Extensions.Logging/` – library
  - `SplunkLoggerProvider` – bounded `Channel<byte[]>` (DropOldest), background worker, periodic flush timer, dispose flush
  - `SplunkLogger` – `ILogger` + `IBufferedLogger`, JSON serialization (`JsonEncodedText` constants)
  - `SplunkLoggerOptions` – options; `SplunkLoggerOptionsValidator` (`IValidateOptions`), `SplunkLoggerOptionsPostConfigure` (endpoint expansion, `HttpClient` + Authorization)
  - `RetryDelegatingHandler` – retry/backoff for the default `HttpClient`
  - `BufferListContent` – `HttpContent` over `List<byte[]>`
  - `SplunkLoggingBuilderExtensions` – `AddSplunk` overloads
- `tests/Splunk.Extensions.Logging.Tests/` – xunit v3 tests

## Build & test

```powershell
dotnet build
dotnet test
```

Keep the build at 0 warnings and all tests passing.

## Conventions

- Follow `.editorconfig` (static fields are `s_camelCase`, private fields `_camelCase`).
- Files use LF line endings (`.gitattributes`); don't introduce CRLF.
- Package versions live in `Directory.Packages.props` (central package management). Avoid adding dependencies to the library.
- Validation goes in `SplunkLoggerOptionsValidator`; option defaulting/derivation goes in `SplunkLoggerOptionsPostConfigure`, not in the provider constructor.
- Required options (`Endpoint`, `Token`, `HttpClient`) are non-nullable with `null!` defaults; don't use `required` (options need a parameterless constructor).
- When changing the event format, update the README mapping table and example and the related test assertions.
- Add comments only where code needs clarification.

## Git

Commit only when asked, never push, and keep commits focused with a descriptive message.
