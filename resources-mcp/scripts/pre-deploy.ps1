$REPOSITORY_ROOT = git rev-parse --show-toplevel

# Each server's Docker build context is its own folder, so it needs its own copy of the shared build props.
foreach ($project in @("MafStarterPack.McpTodo", "MafStarterPack.McpBanking")) {
    pushd $REPOSITORY_ROOT/src/$project

    Copy-Item $REPOSITORY_ROOT/Directory.Build.props .
    Copy-Item $REPOSITORY_ROOT/Directory.Packages.props .

    popd
}
