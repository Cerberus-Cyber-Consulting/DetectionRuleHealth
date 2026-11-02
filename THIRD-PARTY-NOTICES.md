# Third-party notices

Detection Rule Health is MIT-licensed (see [LICENSE](LICENSE)). It ships with the following
third-party components. Their license texts are in [`licenses/`](licenses/) and in the container
image under `/app/licenses/`.

| Component | Used for | License | Text |
|---|---|---|---|
| Kusto Query Language parser (`Microsoft.Azure.Kusto.Language` 12.4.1, NuGet package by Microsoft) | parsing rule KQL | Microsoft Software License Terms, Azure Data Explorer SDK (see below) | `licenses/EULA-Kusto.Language.txt` |
| [.NET runtime](https://github.com/dotnet/runtime) (self-contained) | runtime | MIT | `licenses/MIT-dotnet.txt` |
| [Alpine.js](https://github.com/alpinejs/alpine) | UI behaviour | MIT | `licenses/MIT-alpinejs.txt` |
| [Apache ECharts](https://github.com/apache/echarts) | charts | Apache-2.0, includes parts of d3 (BSD-3-Clause) | `licenses/Apache-2.0.txt`, `licenses/NOTICE-echarts.txt`, `licenses/LICENSE-d3.txt` |
| [Lucide](https://github.com/lucide-icons/lucide) | icons | ISC | `licenses/ISC-lucide.txt` |
| [Montserrat](https://github.com/JulietaUla/Montserrat) | UI font | SIL Open Font License 1.1 | `licenses/OFL-Montserrat.txt` |
| [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono) | code font | SIL Open Font License 1.1 | `licenses/OFL-JetBrainsMono.txt` |

**Kusto Query Language parser.** The parser in the app and the container image is Microsoft's
`Microsoft.Azure.Kusto.Language` NuGet package. It is not covered by the MIT license of Detection Rule
Health: it is licensed by Microsoft under the *Microsoft Software License Terms, Azure Data Explorer
SDK*, included in full in [`licenses/EULA-Kusto.Language.txt`](licenses/EULA-Kusto.Language.txt)
(in the image: `/app/licenses/EULA-Kusto.Language.txt`). By using or redistributing Detection Rule
Health or its image, you agree to those terms for that component. (The parser's source code is also
published by Microsoft under Apache-2.0 at <https://github.com/microsoft/Kusto-Query-Language>; the
binary shipped here is the NuGet package, so its terms apply.)

The .NET runtime carries its own third-party notices:
<https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT>.

The Cerberus Cyber Consulting name, logos and hound mark are not covered by the MIT license; see
the trademark note in the [README](README.md#trademarks).

Microsoft, Microsoft Sentinel, Microsoft Defender XDR, Microsoft Entra and Azure are trademarks of
the Microsoft group of companies. Detection Rule Health is not affiliated with or endorsed by Microsoft.
