# Third-party notices

The source code in this repository is licensed under the MIT License (see `LICENSE`).
The published Docker image additionally contains the third-party components listed below,
each under its own license. This project is not affiliated with any of their authors.

## Base image

| Component | License | Source |
|---|---|---|
| .NET runtime and ASP.NET Core (`mcr.microsoft.com/dotnet/aspnet:10.0`) | MIT | https://github.com/dotnet/dotnet |

The base image also contains a Linux distribution whose packages carry their own licenses;
see https://github.com/dotnet/dotnet-docker for details.

## NuGet packages shipped with the server

| Package | Version | License | Copyright |
|---|---|---|---|
| Azure.Core | 1.50.0 | MIT | © Microsoft Corporation |
| Microsoft.Bcl.AsyncInterfaces | 8.0.0 | MIT | © Microsoft Corporation |
| Microsoft.Graph | 6.8.0 | MIT | Copyright 2019 Microsoft Graph |
| Microsoft.Graph.Core | 4.0.1 | MIT | © Microsoft Corporation |
| Microsoft.IO.RecyclableMemoryStream | 3.0.1 | MIT | © Microsoft Corporation |
| Microsoft.IdentityModel.Abstractions | 8.18.0 | MIT | © Microsoft Corporation |
| Microsoft.IdentityModel.JsonWebTokens | 8.18.0 | MIT | © Microsoft Corporation |
| Microsoft.IdentityModel.Logging | 8.18.0 | MIT | © Microsoft Corporation |
| Microsoft.IdentityModel.Protocols | 8.18.0 | MIT | © Microsoft Corporation |
| Microsoft.IdentityModel.Protocols.OpenIdConnect | 8.18.0 | MIT | © Microsoft Corporation |
| Microsoft.IdentityModel.Tokens | 8.18.0 | MIT | © Microsoft Corporation |
| Microsoft.IdentityModel.Validators | 8.18.0 | MIT | © Microsoft Corporation |
| Microsoft.Kiota.Abstractions | 2.0.0 | MIT | © Microsoft Corporation |
| Microsoft.Kiota.Authentication.Azure | 2.0.0 | MIT | © Microsoft Corporation |
| Microsoft.Kiota.Http.HttpClientLibrary | 2.0.0 | MIT | © Microsoft Corporation |
| Microsoft.Kiota.Serialization.Form | 2.0.0 | MIT | © Microsoft Corporation |
| Microsoft.Kiota.Serialization.Json | 2.0.0 | MIT | © Microsoft Corporation |
| Microsoft.Kiota.Serialization.Multipart | 2.0.0 | MIT | © Microsoft Corporation |
| Microsoft.Kiota.Serialization.Text | 2.0.0 | MIT | © Microsoft Corporation |
| Std.UriTemplate | 2.0.8 | Apache-2.0 | Std.UriTemplate authors |
| System.ClientModel | 1.8.0 | MIT | © Microsoft Corporation |
| System.IdentityModel.Tokens.Jwt | 8.18.0 | MIT | © Microsoft Corporation |
| System.Memory.Data | 8.0.1 | MIT | © Microsoft Corporation |
| YamlDotNet | 18.1.0 | MIT | Copyright (c) Antoine Aubry and contributors |

Versions are those resolved at the time of writing; regenerate with
`dotnet list src/MockGraph.Server package --include-transitive` after dependency updates.

## License texts

- MIT: the text in `LICENSE` applies to each MIT component with that component's own copyright notice above.
- Apache-2.0: `LICENSES/Apache-2.0.txt`.

## Fixture content

The documents in `fixtures/` (including the generated `.docx`, `.pdf`, `.xlsx` and `.pptx` files in `fixtures/files/company/`) are original content written for this project under the MIT License. They describe a fictional company and contain no third-party text. The Python libraries used to generate them (`tools/fixture-files/`) are not part of the image.

Packages used only for building and testing (xUnit, Microsoft.NET.Test.Sdk, Microsoft.AspNetCore.Mvc.Testing,
Xunit.SkippableFact, coverlet) are not distributed with the image.
