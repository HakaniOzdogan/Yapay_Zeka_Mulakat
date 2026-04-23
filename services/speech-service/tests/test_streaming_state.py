from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from app.streaming_state import HypothesisSegment, StreamingCommitState, apply_decode_result, build_hypothesis_segments


def _joined_text(segments: list[HypothesisSegment]) -> str:
    return " ".join(segment.text for segment in segments).strip()


def test_common_prefix_is_committed_after_second_matching_decode() -> None:
    state = StreamingCommitState()

    first = [
        HypothesisSegment(start_ms=0, end_ms=900, text="Merhaba"),
        HypothesisSegment(start_ms=900, end_ms=1800, text="nasilsin"),
    ]
    second = [
        HypothesisSegment(start_ms=0, end_ms=900, text="Merhaba"),
        HypothesisSegment(start_ms=900, end_ms=1800, text="nasilsin"),
        HypothesisSegment(start_ms=1800, end_ms=2500, text="bugun"),
    ]

    update1 = apply_decode_result(state, first, force_finalize=False, agreement_passes=2)
    assert [segment.text for segment in update1.final_segments] == []
    assert [segment.text for segment in update1.partial_segments] == ["Merhaba", "nasilsin"]

    update2 = apply_decode_result(state, second, force_finalize=False, agreement_passes=2)
    assert [segment.text for segment in update2.final_segments] == ["Merhaba", "nasilsin"]
    assert [segment.text for segment in update2.partial_segments] == ["bugun"]
    assert state.last_committed_end_ms == 1800


def test_promoted_final_only_contains_new_delta() -> None:
    state = StreamingCommitState()

    first = [HypothesisSegment(start_ms=0, end_ms=800, text="Hello this is")]
    second = [HypothesisSegment(start_ms=0, end_ms=1600, text="Hello this is a live transcript")]
    third = [HypothesisSegment(start_ms=0, end_ms=2400, text="Hello this is a live transcript verification run")]
    fourth = [HypothesisSegment(start_ms=0, end_ms=2600, text="Hello this is a live transcript verification run today")]

    apply_decode_result(state, first, force_finalize=False, agreement_passes=2)
    update2 = apply_decode_result(state, second, force_finalize=False, agreement_passes=2)
    apply_decode_result(state, third, force_finalize=False, agreement_passes=2)
    update3 = apply_decode_result(state, fourth, force_finalize=False, agreement_passes=2)

    assert _joined_text(update2.final_segments) == "Hello this is"
    assert _joined_text(update3.final_segments) == "a live transcript verification run"


def test_unstable_suffix_stays_partial() -> None:
    state = StreamingCommitState()

    apply_decode_result(
        state,
        [HypothesisSegment(start_ms=0, end_ms=900, text="Teknik"), HypothesisSegment(start_ms=900, end_ms=1800, text="mulakat")],
        force_finalize=False,
        agreement_passes=2,
    )
    update = apply_decode_result(
        state,
        [HypothesisSegment(start_ms=0, end_ms=900, text="Teknik"), HypothesisSegment(start_ms=900, end_ms=1800, text="gorusme")],
        force_finalize=False,
        agreement_passes=2,
    )

    assert [segment.text for segment in update.final_segments] == ["Teknik"]
    assert [segment.text for segment in update.partial_segments] == ["gorusme"]


def test_punctuation_and_segmentation_changes_do_not_break_commit() -> None:
    state = StreamingCommitState()

    first = [
        HypothesisSegment(start_ms=0, end_ms=1000, text="Hello this is"),
        HypothesisSegment(start_ms=1000, end_ms=1800, text="a test"),
    ]
    second = [
        HypothesisSegment(start_ms=0, end_ms=1200, text="Hello, this is a"),
        HypothesisSegment(start_ms=1200, end_ms=2200, text="test today"),
    ]

    apply_decode_result(state, first, force_finalize=False, agreement_passes=2)
    update = apply_decode_result(state, second, force_finalize=False, agreement_passes=2)

    assert _joined_text(update.final_segments) == "Hello, this is a test"
    assert _joined_text(update.partial_segments) == "today"


def test_force_finalize_commits_remaining_tail() -> None:
    state = StreamingCommitState()
    first = [HypothesisSegment(start_ms=0, end_ms=900, text="kalan"), HypothesisSegment(start_ms=900, end_ms=1500, text="cumle")]

    apply_decode_result(state, first, force_finalize=False, agreement_passes=2)
    update = apply_decode_result(state, [], force_finalize=True, agreement_passes=2)

    assert [segment.text for segment in update.final_segments] == ["kalan", "cumle"]
    assert update.partial_segments == []
    assert state.last_committed_end_ms == 1500


def test_force_finalize_preserves_shifted_overlap_prefix() -> None:
    state = StreamingCommitState()
    first = [
        HypothesisSegment(start_ms=0, end_ms=2600, text="Hello this is a live transcript verification run"),
        HypothesisSegment(start_ms=2600, end_ms=5200, text="We are checking partial and final transcript"),
    ]
    shifted = [
        HypothesisSegment(start_ms=1800, end_ms=5200, text="a live transcript verification run we are checking partial and final transcript behavior"),
        HypothesisSegment(start_ms=5200, end_ms=7600, text="over the websocket service"),
    ]

    apply_decode_result(state, first, force_finalize=False, agreement_passes=2)
    update = apply_decode_result(state, shifted, force_finalize=True, agreement_passes=2)

    final_text = _joined_text(update.final_segments).lower()
    assert "hello this is a live transcript verification run" in final_text
    assert "we are checking partial and final transcript behavior over the websocket service" in final_text


def test_shifted_window_keeps_early_content_in_partial_until_finalize() -> None:
    state = StreamingCommitState()
    first = [
        HypothesisSegment(start_ms=0, end_ms=2500, text="Hello this is a live transcript verification run"),
        HypothesisSegment(start_ms=2500, end_ms=4200, text="We are checking partial and final transcript"),
    ]
    shifted = [
        HypothesisSegment(start_ms=1600, end_ms=5000, text="a live transcript verification run we are checking partial and final transcript behavior"),
        HypothesisSegment(start_ms=5000, end_ms=7000, text="over the websocket service"),
    ]

    apply_decode_result(state, first, force_finalize=False, agreement_passes=2)
    update = apply_decode_result(state, shifted, force_finalize=False, agreement_passes=2)

    visible_text = f"{_joined_text(update.final_segments)} {_joined_text(update.partial_segments)}".strip()
    assert "Hello this is" in visible_text
    assert "over the websocket service" in visible_text


def test_duplicate_committed_leading_segment_is_dropped() -> None:
    committed = [HypothesisSegment(start_ms=0, end_ms=1000, text="Merhaba")]
    normalized = build_hypothesis_segments(
        [
            {"start_ms": 900, "end_ms": 1600, "text": "Merhaba"},
            {"start_ms": 1600, "end_ms": 2200, "text": "dunya"},
        ],
        last_committed_end_ms=1000,
        committed_segments=committed,
    )

    assert [segment.text for segment in normalized] == ["dunya"]


def test_empty_and_old_segments_are_filtered() -> None:
    normalized = build_hypothesis_segments(
        [
            {"start_ms": 0, "end_ms": 200, "text": "   "},
            {"start_ms": 0, "end_ms": 400, "text": "eski"},
            {"start_ms": 450, "end_ms": 900, "text": "yeni"},
        ],
        last_committed_end_ms=400,
        committed_segments=[],
    )

    assert [segment.text for segment in normalized] == ["yeni"]
