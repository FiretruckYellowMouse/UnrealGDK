#!/usr/bin/env bash

### This script should only be run on Improbable's internal build machines.
### If you don't work at Improbable, this may be interesting as a guide to what software versions we use for our
### automation, but not much more than that.

set -e -u -o pipefail

if [[ -n "${DEBUG-}" ]]; then
  set -x
fi

if [[ -z "$BUILDKITE" ]]; then
  echo "This script is only intended to be run on Improbable CI."
  exit 1
fi

cd "$(dirname "$0")/../"

source ci/common-release.sh

setupReleaseTool

mkdir -p ./logs

# This assigns the first argument passed to this script to the variable REPO
REPO="${1}"
# This assigns the gdk-version key that was set in .buildkite\release.steps.yaml to the variable GDK-VERSION
GDK_VERSION="$(buildkite-agent meta-data get gdk-version)"
# This assigns the engine-version key that was set in .buildkite\release.steps.yaml to the variable ENGINE-VERSION
ENGINE_VERSIONS="$(buildkite-agent meta-data get engine-version)"

### TODO: ReleaseCommand.cs ingests the following:
### "version" = "The version that is being released"
### This always corresponds to GDK_VERSION.

### "source-branch" = "The source branch name from which we are cutting the candidate."
### In GDK, Example Project and TestGyms this is `master`
### In UnrealEngine this is "ENGINE_VERSION", where ENGINE_VERSIONS is IFS iterated over.

### "candidate-branch" = "The candidate branch name."
### In GDK, Example Project and TestGyms this is "GDK_VERSION-rc"
### In UnrealEngine this must be compiled from "ENGINE_VERSIONS-GDK_VERSION-rc", where ENGINE_VERSIONS is IFS iterated over.

### "target-branch" = "The name of the branch into which we are merging the candidate."
### In GDK, Example Project and TestGyms this is `release`
### In UnrealEngine this is "ENGINE_VERSION", where ENGINE_VERSIONS is IFS iterated over.

while IFS= read -r ENGINE_VERSIONS; do

  docker run \
    -v "${SECRETS_DIR}":/var/ssh \
    -v "${SECRETS_DIR}":/var/github \
    -v "$(pwd)"/logs:/var/logs \
    local:gdk-release-tool \
        prep "${GDK_VERSION}" \
        --git-repository-name="${REPO}" \
        --engine-versions="${ENGINE_VERSIONS}" \
        --github-key-file="/var/github/github_token" \
        --buildkite-metadata-path="/var/logs/bk-metadata" ${PIN_ARG}

done <<< "${ENGINE_VERSIONS}"

echo "--- Writing metadata :pencil2:"
writeBuildkiteMetadata "./logs/bk-metadata"

if [[ "${REPO}" == "UnrealEngine" ]]; then
echo "--- Preparing ${REPO} @ ${ENGINE_VERSIONS}, ${RELEASE_VERSION} :package:"
else
echo "--- Preparing ${REPO} @ ${RELEASE_VERSION} :package:"
fi 
