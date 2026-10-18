#!/usr/bin/env bash
# Regenerates the Kiota clients from the product's committed OpenAPI documents (ADR-0037).
# Generated code is committed and never edited by hand; tooling-ci runs this script and fails if
# the result differs from what is committed. Usage, from anywhere: bash tooling/clients/generate.sh
set -euo pipefail
cd "$(dirname "$0")/../.."   # the repository root: Kiota records paths relative to it

export KIOTA_OFFLINE_ENABLED=true KIOTA_TUTORIAL_ENABLED=false
dotnet tool restore > /dev/null

generate() { # client name, OpenAPI document, namespace
  dotnet kiota generate \
    --language CSharp \
    --openapi "$2" \
    --class-name "$1Client" \
    --namespace-name "$3" \
    --output "tooling/clients/$1" \
    --exclude-backward-compatible \
    --clean-output \
    --log-level Warning
  rm -f "tooling/clients/$1/.kiota.log"
}

generate BackOfficeApi product/src/Hosts/BackOfficeApi/openapi/BackOfficeApi.json EventTicketing.Tooling.Clients.BackOffice
generate PublicApi product/src/Hosts/PublicApi/openapi/PublicApi.json EventTicketing.Tooling.Clients.Public
