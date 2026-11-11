#!/usr/bin/env python3
"""Sets the assistant's lore of a Quasar server or cluster from the command line.

    quasar-lore.py list
    quasar-lore.py show TARGET
    quasar-lore.py set-text TARGET FILE      (FILE or - for stdin)
    quasar-lore.py set-git TARGET URL [--git-token-env NAME | --remove-git-token]
    quasar-lore.py refresh TARGET
    quasar-lore.py clear TARGET

TARGET is the unique name of a server or cluster. URL is the skill folder in a
Git repository, such as https://github.com/owner/repo/tree/main/skills/lore.

Quasar's address comes from --url or QUASAR_URL, and its API token from the
environment variable --token-env names (QUASAR_API_TOKEN by default), as for
`Quasar cluster`. A Git access token for a private repository is read from the
variable --git-token-env names, never from the command line.

Exit codes: 0 done, 1 Quasar refused the request, 2 bad usage, 3 the lore was
saved but its Git skill could not be fetched.
"""

import argparse
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request

ROUTE = "/api/v1/assistant/lore"


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Set the assistant's lore of a Quasar server or cluster."
    )
    parser.add_argument("--url", default=os.environ.get("QUASAR_URL"))
    parser.add_argument("--token-env", default="QUASAR_API_TOKEN")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("list")
    for name in ("show", "refresh", "clear"):
        commands.add_parser(name).add_argument("target")
    text = commands.add_parser("set-text")
    text.add_argument("target")
    text.add_argument("file")
    git = commands.add_parser("set-git")
    git.add_argument("target")
    git.add_argument("git_url")
    token = git.add_mutually_exclusive_group()
    token.add_argument("--git-token-env")
    token.add_argument("--remove-git-token", action="store_true")
    args = parser.parse_args()
    if not args.url:
        parser.error("give Quasar's address with --url or QUASAR_URL")

    target = getattr(args, "target", "")
    path = ROUTE + ("/" + urllib.parse.quote(target, safe="") if target else "")
    if args.command in ("list", "show"):
        result = call(args, "GET", ROUTE)
        if args.command == "show":
            key = target.lower().removeprefix("cluster:")
            result = next((v for v in result if v["target"].lower() == key), None)
            if result is None:
                print(f"{target} has no lore.", file=sys.stderr)
                return 1
        print(json.dumps(result, indent=2))
        return 0
    if args.command == "clear":
        call(args, "DELETE", path)
        print(f"{target} has no lore now.")
        return 0
    if args.command == "refresh":
        return report(call(args, "POST", path + "/refresh"))
    if args.command == "set-text":
        body = {"kind": "text", "text": read(args.file)}
    else:
        body = {"kind": "git", "gitUrl": args.git_url}
        if args.remove_git_token:
            body["token"] = ""
        elif args.git_token_env:
            body["token"] = os.environ.get(args.git_token_env)
            if not body["token"]:
                parser.error(f"{args.git_token_env} is empty")
    return report(call(args, "PUT", path, body))


def read(file: str) -> str:
    if file == "-":
        return sys.stdin.read()
    with open(file, encoding="utf-8") as stream:
        return stream.read()


def report(view: dict) -> int:
    print(json.dumps(view, indent=2))
    if view.get("error"):
        print(f"The Git skill could not be fetched: {view['error']}", file=sys.stderr)
        return 3
    return 0


def call(args, method: str, path: str, body: dict | None = None):
    request = urllib.request.Request(
        args.url.rstrip("/") + path,
        method=method,
        data=None if body is None else json.dumps(body).encode(),
        headers={"Accept": "application/json"},
    )
    if body is not None:
        request.add_header("Content-Type", "application/json")
    token = os.environ.get(args.token_env)
    if token:
        request.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(request, timeout=180) as response:
            text = response.read().decode()
            return json.loads(text) if text else None
    except urllib.error.HTTPError as error:
        detail = error.read().decode(errors="replace")
        try:
            detail = json.loads(detail).get("detail") or detail
        except (ValueError, AttributeError):
            pass
        print(f"HTTP {error.code}: {detail}", file=sys.stderr)
        sys.exit(1)
    except urllib.error.URLError as error:
        print(f"Can't reach Quasar at {args.url}: {error.reason}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    sys.exit(main())
