# Third-party components

- .NET 10 runtime: Microsoft, MIT license. https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- Windows App SDK (WinUI, Foundation, InteractiveExperiences, DWrite components): Microsoft, MIT license and component-specific notices included in the NuGet distribution. https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE
- Npgsql 10.0.3: PostgreSQL License. https://github.com/npgsql/npgsql/blob/main/LICENSE
- Microsoft.Extensions.Logging.Abstractions and Microsoft.Extensions.DependencyInjection.Abstractions 10.0.0 (used by Npgsql): Microsoft .NET, MIT.
- System.Security.Cryptography.ProtectedData: Microsoft .NET, MIT.
- Windows system fonts are referenced from Windows and are not redistributed.

No PostgreSQL server, pgvector extension binaries or browser runtime is bundled. The Docker images used by the checks are separate from application delivery.

- Manrope variable font: Copyright 2018 The Manrope Project Authors, SIL Open Font License 1.1. Bundled license: Assets/LICENSE-Manrope.txt. Identity wordmarks are outlined exports.
