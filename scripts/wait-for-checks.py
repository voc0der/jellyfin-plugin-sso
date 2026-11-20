#!/usr/bin/env python3
"""Wait for the other workflows on the exact push being released."""

import json
import os
import subprocess
import time

REQUIRED = {"Lint", "Settings page", "CodeQL", "Dependency Review"}


def check_status(runs):
    # API results are newest first; an older attempt must not mask a rerun.
    latest = {}
    for run in runs:
        if run["name"] in REQUIRED and run["event"] == "push":
            latest.setdefault(run["name"], run)
    failed = [name for name, run in latest.items()
              if run["status"] == "completed" and run["conclusion"] != "success"]
    if failed:
        raise RuntimeError(f"Release checks failed: {', '.join(sorted(failed))}")
    return REQUIRED == latest.keys() and all(
        run["status"] == "completed" and run["conclusion"] == "success"
        for run in latest.values()
    )


def main():
    repo = os.environ["GITHUB_REPOSITORY"]
    sha = os.environ["GITHUB_SHA"]
    for _ in range(60):
        result = subprocess.check_output([
            "gh", "api", f"repos/{repo}/actions/runs?head_sha={sha}&event=push&per_page=100"
        ], text=True)
        if check_status(json.loads(result)["workflow_runs"]):
            print("All release checks passed.")
            return
        time.sleep(15)
    raise TimeoutError("Required checks did not all finish successfully within 15 minutes")


if __name__ == "__main__":
    main()
