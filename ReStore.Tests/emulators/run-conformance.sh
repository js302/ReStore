#!/usr/bin/env bash
# Runs the storage conformance suite (EnvConfiguredStorageConformanceTests) against local emulators.
#
#   ReStore.Tests/emulators/run-conformance.sh [provider ...]      # default: s3 b2 azure gcp sftp
#
# Requires docker and the .NET SDK. Each emulator is started on a free localhost port, the suite is run once per
# provider, and the container is always removed again (also on Ctrl-C / failure).
# Environment:
#   IMAGE_PREFIX      registry prefix for the Docker Hub images (default empty; e.g. "mirror.gcr.io/" behind a Hub-blocking proxy)
#   COVERAGE_DIR      if set, collects cobertura coverage into this directory (one sub-folder per provider)
#   DOTNET_TEST_ARGS  extra arguments for `dotnet test`
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
proj="$here/../ReStore.Tests.csproj"
prefix="${IMAGE_PREFIX:-}"
providers=("$@")
[ ${#providers[@]} -eq 0 ] && providers=(s3 b2 azure gcp sftp)

containers=()
cleanup() { for c in "${containers[@]:-}"; do [ -n "$c" ] && docker rm -f "$c" >/dev/null 2>&1; done; }
trap cleanup EXIT
trap 'exit 130' INT TERM

free_port() { python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()'; }

wait_for() { # <description> <command...>
  local what="$1"; shift
  for _ in $(seq 1 60); do "$@" >/dev/null 2>&1 && return 0; sleep 1; done
  echo "timed out waiting for $what" >&2; return 1
}

failed=0
for provider in "${providers[@]}"; do
  echo "=== $provider ==="
  port="$(free_port)"; name="restore-conf-$provider-$$"; type="$provider"; env_extra=()
  case "$provider" in
    s3|b2)
      docker run -d --name "$name" -p "127.0.0.1:$port:9090" -e initialBuckets=restore-conf -e COM_ADOBE_TESTING_S3MOCK_STORE_INITIAL_BUCKETS=restore-conf "${prefix}adobe/s3mock" >/dev/null || { failed=1; continue; }
      containers+=("$name")
      wait_for "S3 emulator" curl -s -o /dev/null "http://127.0.0.1:$port/" || { failed=1; continue; }
      if [ "$provider" = s3 ]; then
        options="{\"accessKeyId\":\"test\",\"secretAccessKey\":\"test\",\"region\":\"us-east-1\",\"bucketName\":\"restore-conf\",\"serviceUrl\":\"http://127.0.0.1:$port\"}"
      else
        options="{\"keyId\":\"test\",\"applicationKey\":\"test\",\"bucketName\":\"restore-conf\",\"serviceUrl\":\"http://127.0.0.1:$port\"}"
      fi ;;
    azure)
      docker run -d --name "$name" -p "127.0.0.1:$port:10000" "mcr.microsoft.com/azure-storage/azurite" \
        azurite-blob --blobHost 0.0.0.0 --skipApiVersionCheck >/dev/null || { failed=1; continue; }
      containers+=("$name")
      wait_for "Azurite" curl -s -o /dev/null "http://127.0.0.1:$port/" || { failed=1; continue; }
      key='Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=='
      options="{\"connectionString\":\"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=$key;BlobEndpoint=http://127.0.0.1:$port/devstoreaccount1;\",\"containerName\":\"restore-conf\"}" ;;
    gcp)
      # fake-gcs-server builds upload URLs from -external-url, so the host port must equal the container port.
      docker run -d --name "$name" -p "127.0.0.1:$port:$port" "${prefix}fsouza/fake-gcs-server" \
        -scheme http -port "$port" -external-url "http://127.0.0.1:$port" -backend memory >/dev/null || { failed=1; continue; }
      containers+=("$name")
      wait_for "fake-gcs-server" curl -sf "http://127.0.0.1:$port/storage/v1/b" || { failed=1; continue; }
      curl -sf -X POST "http://127.0.0.1:$port/storage/v1/b" -H 'Content-Type: application/json' -d '{"name":"restore-conf"}' >/dev/null || { failed=1; continue; }
      options='{"bucketName":"restore-conf"}'
      env_extra=("STORAGE_EMULATOR_HOST=http://127.0.0.1:$port/storage/v1/") ;;
    sftp)
      # linuxserver/openssh-server is multi-arch (also runs on arm64 hosts) and gives the user a writable home.
      docker run -d --name "$name" -p "127.0.0.1:$port:2222" -e PASSWORD_ACCESS=true -e USER_NAME=user -e USER_PASSWORD=pass \
        "${prefix}linuxserver/openssh-server" >/dev/null || { failed=1; continue; }
      containers+=("$name")
      wait_for "sshd" bash -c "exec 3<>/dev/tcp/127.0.0.1/$port && head -c 4 <&3 | grep -q SSH" || { failed=1; continue; }
      options="{\"host\":\"127.0.0.1\",\"port\":\"$port\",\"username\":\"user\",\"password\":\"pass\"}" ;;
    *) echo "unknown provider '$provider' (s3 b2 azure gcp sftp)" >&2; failed=1; continue ;;
  esac

  args=(test "$proj" --filter "FullyQualifiedName~EnvConfiguredStorageConformanceTests")
  [ -n "${COVERAGE_DIR:-}" ] && args+=(--collect:"XPlat Code Coverage" --results-directory "$COVERAGE_DIR/$provider")
  # shellcheck disable=SC2086
  env RESTORE_CONFORMANCE_TYPE="$type" RESTORE_CONFORMANCE_OPTIONS="$options" \
    "${env_extra[@]}" dotnet "${args[@]}" ${DOTNET_TEST_ARGS:-} || failed=1

  docker rm -f "$name" >/dev/null 2>&1
done
exit $failed
