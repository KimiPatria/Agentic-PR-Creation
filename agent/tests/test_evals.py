"""The eval scorer is itself code that can be wrong, so its judgement is tested here."""

from __future__ import annotations

from pr_agent.evals import check_case, load_cases

KNOWN = {"materialGroup": {"IT01", "IT10"}, "costCenter": {"CC1101", "CC1103"}, "wbs": set(), "assetNumber": set(), "materialNumber": set(), "uom": {"EA", "AU"}}


def _failed(checks):
    return [name for name, passed, _ in checks if not passed]


def test_every_case_file_entry_is_well_formed():
    cases = load_cases(None)
    assert len(cases) == 20
    assert len({c["id"] for c in cases}) == 20
    assert all(c["need"] and c["lines"] for c in cases)


def test_a_correct_proposal_passes_every_check():
    case = {"id": "x", "need": "n", "lines": [{"item_type": "Material", "material_group": ["IT01"], "quantity": 5}]}
    proposal = {"lines": [{"lineNo": 1, "itemType": "material", "materialGroup": "it01", "quantity": 5.0, "uom": "EA"}]}

    assert _failed(check_case(case, proposal, "", KNOWN)) == []


def test_no_proposal_fails():
    assert _failed(check_case({"id": "x", "need": "n", "lines": [{}]}, None, "hello", KNOWN)) == ["proposed a form fill"]


def test_wrong_value_wrong_line_count_and_invented_code_all_fail():
    case = {"id": "x", "need": "n", "lines": [{"item_type": "Service", "material_group": ["IT10"]}]}
    proposal = {"lines": [{"lineNo": 1, "itemType": "Material", "materialGroup": "ZZ99"}, {"lineNo": 2, "itemType": "Service"}]}

    failed = _failed(check_case(case, proposal, "", KNOWN))

    assert "line count" in failed
    assert "line 1 materialGroup exists in SAP" in failed
    assert "line 1 item_type" in failed
    assert "line 1 material_group" in failed


def test_a_guessed_cost_center_fails_when_the_requestor_gave_none():
    case = {"id": "x", "need": "n", "lines": [{"item_type": "Material"}], "must_be_empty": ["cost_center"]}
    proposal = {"lines": [{"lineNo": 1, "itemType": "Material", "costCenter": "CC1101"}]}

    assert _failed(check_case(case, proposal, "", KNOWN)) == ["cost_center left empty"]


def test_split_and_reply_checks():
    case = {"id": "x", "need": "n", "lines": [{}], "expect_split": True, "reply_must_match": "(?i)split", "reply_must_not_match": "(?i)i have submitted"}
    proposal = {"lines": [{"lineNo": 1}], "check": {"sapPrCount": 1}}

    failed = _failed(check_case(case, proposal, "I have submitted it.", KNOWN))

    assert failed == ["lines split into several PRs", "reply mentions it", "reply makes no false claim"]
