# syntax=docker/dockerfile:1.7
#
# Detection Rule Health — container image
#   docker build --build-arg VERSION=0.1.0-beta.1 -t detectionrulehealth .
#
# Build stage runs on the build machine's architecture and cross-publishes for the target,
# so multi-arch builds need no QEMU. The final stage is Ubuntu "chiseled": no shell, no package
# manager, non-root. That is also why it has no RUN instructions and the HEALTHCHECK uses exec form.

ARG DOTNET_VERSION=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-noble AS build
ARG TARGETARCH
ARG VERSION=0.0.0-dev
WORKDIR /src

# Restore first so the package layer is cached between source changes.
# global.json pins the SDK band, nuget.config pins nuget.org as the only package source.
COPY global.json nuget.config ./
COPY src/RuleHealth.Core/RuleHealth.Core.csproj src/RuleHealth.Core/
COPY src/RuleHealth.Cli/RuleHealth.Cli.csproj  src/RuleHealth.Cli/
# Same self-contained settings as the publish below, or restore skips the Linux runtime pack.
RUN dotnet restore src/RuleHealth.Cli/RuleHealth.Cli.csproj -a "$TARGETARCH" \
      -p:SelfContained=true -p:PublishSingleFile=true -p:UseAppHost=true

COPY src/ src/
# UseAppHost=true: the project turns the native launcher off for Windows dev machines (application
# control blocks unsigned exes there); the single-file Linux binary /app/rulehealth needs it.
RUN dotnet publish src/RuleHealth.Cli/RuleHealth.Cli.csproj \
      -c Release -a "$TARGETARCH" --no-restore \
      --self-contained true \
      -p:UseAppHost=true \
      -p:PublishSingleFile=true \
      -p:InvariantGlobalization=true \
      -p:Version="$VERSION" \
      -p:DebugType=none \
      -o /out \
 && mkdir -p /data-skel

FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION}-noble-chiseled AS final
ARG VERSION=0.0.0-dev

LABEL org.opencontainers.image.title="Detection Rule Health" \
      org.opencontainers.image.description="Local overview of Microsoft Sentinel analytics rules and Defender XDR custom detections: efficacy and value checks." \
      org.opencontainers.image.vendor="Cerberus Cyber Consulting BV" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.source="https://github.com/Cerberus-Cyber-Consulting/DetectionRuleHealth" \
      org.opencontainers.image.documentation="https://cerberus-cyber-consulting.github.io/DetectionRuleHealth/" \
      org.opencontainers.image.version="${VERSION}"

COPY --from=build /out/rulehealth          /app/rulehealth
COPY ui/                                   /app/ui/
COPY samples/                              /app/samples/
COPY LICENSE THIRD-PARTY-NOTICES.md        /app/
COPY licenses/                             /app/licenses/
# /data must exist and belong to the app user: a new named volume copies this ownership.
COPY --from=build --chown=1654:1654 /data-skel /data

ENV RULEHEALTH_DATA=/data \
    RULEHEALTH_SAMPLES=/app/samples \
    RULEHEALTH_UI=/app/ui \
    RULEHEALTH_LISTEN=http://+:5180/ \
    RULEHEALTH_PUBLIC_URL=http://localhost:5180 \
    RULEHEALTH_UPDATE_CHECK=true \
    DOTNET_EnableDiagnostics=0

WORKDIR /app
VOLUME ["/data"]
EXPOSE 5180
# uid 1654 = "app", the non-root user that ships in the .NET chiseled images.
USER 1654

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["/app/rulehealth", "health"]

ENTRYPOINT ["/app/rulehealth"]
CMD ["serve"]
