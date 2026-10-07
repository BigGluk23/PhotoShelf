#!/usr/bin/env python3
"""Fail-closed validation of live GitHub release controls. Never reads secret values or writes GitHub."""
import argparse
import json
from pathlib import Path

REPOSITORY = "BigGluk23/PhotoShelf"
ENVIRONMENT = "release"
OWNER = "BigGluk23"
OWNER_ID = 205813777


def verify_environment(environment, branches, non_environment_secret_present, actor, triggering_actor, run_attempt):
    if non_environment_secret_present is not False:
        raise ValueError("Signing key must not be available at repository or organization scope")
    if not actor or not triggering_actor:
        raise ValueError("Release actor identity is unavailable")
    if run_attempt != 1:
        raise ValueError("Release reruns cannot reuse an old approval; start a fresh manual workflow dispatch")
    if (environment.get("name") != ENVIRONMENT or
            environment.get("url") != f"https://api.github.com/repos/{REPOSITORY}/environments/{ENVIRONMENT}" or
            type(environment.get("id")) is not int or environment["id"] <= 0):
        raise ValueError("The expected release environment is missing or ambiguous")
    rules = environment.get("protection_rules")
    if not isinstance(rules, list):
        raise ValueError("Release environment protection rules are unavailable")
    required = [rule for rule in rules if rule.get("type") == "required_reviewers"]
    if len(required) > 1:
        raise ValueError("Release environment has ambiguous reviewer protection")
    owner_approval_required = len(required) == 1
    if owner_approval_required:
        if required[0].get("prevent_self_review") is not True:
            raise ValueError("Protected release mode must prevent self-review")
        reviewers = required[0].get("reviewers")
        if (not isinstance(reviewers, list) or len(reviewers) != 1 or reviewers[0].get("type") != "User" or
                reviewers[0].get("reviewer", {}).get("login", "").casefold() != OWNER.casefold() or
                reviewers[0].get("reviewer", {}).get("id") != OWNER_ID):
            raise ValueError("The sole required release reviewer must be the repository owner BigGluk23")
        if OWNER.casefold() in (actor.casefold(), triggering_actor.casefold()):
            raise ValueError("A different collaborator must dispatch a protected release so the owner can approve it")
    elif actor.casefold() != OWNER.casefold() or triggering_actor.casefold() != OWNER.casefold():
        raise ValueError("Only the repository owner may dispatch a release in solo mode")
    policy = environment.get("deployment_branch_policy")
    if not isinstance(policy, dict) or policy.get("protected_branches") is not False or policy.get("custom_branch_policies") is not True:
        raise ValueError("Release must use an explicit selected-branch policy")
    entries = branches.get("branch_policies")
    if (branches.get("total_count") != 1 or not isinstance(entries, list) or len(entries) != 1 or
            entries[0].get("name") != "main" or entries[0].get("type") != "branch"):
        raise ValueError("Only the exact main branch may deploy to release; tags and wildcard rules are forbidden")
    # GitHub documents the UI setting, but not this field in the REST response contract.
    # Reject an explicit unsafe value; absence must be checked by the owner in Settings,
    # rather than being misrepresented as proof that administrator bypass is disabled.
    if "can_admins_bypass" in environment and environment["can_admins_bypass"] is not False:
        raise ValueError("Administrator bypass of release protection must be disabled")
    return {"environmentId": environment["id"], "owner": OWNER,
            "releaseMode": "protected" if owner_approval_required else "solo",
            "ownerApprovalRequired": owner_approval_required,
            "administratorBypassVerifiedByApi": environment.get("can_admins_bypass") is False}


def verify_owner_approval(history, environment_id):
    if not isinstance(history, list):
        raise ValueError("Release approval history is unavailable")
    relevant = []
    for review in history:
        environments = review.get("environments", [])
        if any(environment.get("id") == environment_id and environment.get("name") == ENVIRONMENT
               for environment in environments):
            relevant.append(review)
    if (len(relevant) != 1 or relevant[0].get("state") != "approved" or
            relevant[0].get("user", {}).get("login", "").casefold() != OWNER.casefold() or
            relevant[0].get("user", {}).get("id") != OWNER_ID):
        raise ValueError("This release run needs one explicit owner approval for the exact release environment")
    return {"environmentId": environment_id, "approvedBy": OWNER}


def read_json(path):
    path = Path(path)
    if path.stat().st_size > 4 * 1024 * 1024:
        raise ValueError("Release control response exceeds supported bounds")
    return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--environment", type=Path, required=True)
    parser.add_argument("--branches", type=Path, required=True)
    parser.add_argument("--non-environment-secret-present", choices=("true", "false"), required=True)
    parser.add_argument("--actor", required=True)
    parser.add_argument("--triggering-actor", required=True)
    parser.add_argument("--run-attempt", type=int, required=True)
    parser.add_argument("--expected-environment-id", type=int)
    parser.add_argument("--approvals", type=Path)
    args = parser.parse_args()
    try:
        result = verify_environment(read_json(args.environment), read_json(args.branches),
                                    args.non_environment_secret_present == "true", args.actor,
                                    args.triggering_actor, args.run_attempt)
        if args.expected_environment_id is not None and result["environmentId"] != args.expected_environment_id:
            raise ValueError("Release environment was replaced after validation")
        owner_approval_verified = False
        if args.approvals and result["ownerApprovalRequired"]:
            verify_owner_approval(read_json(args.approvals), result["environmentId"])
            owner_approval_verified = True
        print(json.dumps(dict(result, status="passed", ownerApprovalVerified=owner_approval_verified)))
        return 0
    except (ValueError, OSError, TypeError, KeyError, AttributeError) as error:
        print("FAILED release protection: " + str(error))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
