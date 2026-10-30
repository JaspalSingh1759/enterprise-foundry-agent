#!/usr/bin/env bash

REPOSITORY_ROOT=$(git rev-parse --show-toplevel)

# Each server's Docker build context is its own folder, so it needs its own copy of the shared build props.
for project in MafStarterPack.McpTodo MafStarterPack.McpBanking; do
    pushd "$REPOSITORY_ROOT/src/$project"

    cp "$REPOSITORY_ROOT/Directory.Build.props" .
    cp "$REPOSITORY_ROOT/Directory.Packages.props" .

    popd
done
