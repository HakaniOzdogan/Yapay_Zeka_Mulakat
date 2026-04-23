from __future__ import annotations

import re
from dataclasses import dataclass, field


_WHITESPACE_RE = re.compile(r"\s+")
_TOKEN_RE = re.compile(r"\w+", re.UNICODE)


@dataclass(frozen=True)
class HypothesisSegment:
    start_ms: int
    end_ms: int
    text: str

    @property
    def stable_text(self) -> str:
        return _normalize_text(self.text)

    def to_payload(self) -> dict[str, int | str]:
        return {
            "start_ms": self.start_ms,
            "end_ms": self.end_ms,
            "text": self.text,
        }


@dataclass
class CommitUpdate:
    final_segments: list[HypothesisSegment] = field(default_factory=list)
    partial_segments: list[HypothesisSegment] = field(default_factory=list)
    carry_over_segments: list[HypothesisSegment] = field(default_factory=list)
    agreement_mode: str = "text_prefix"

    @property
    def partial_text(self) -> str:
        return " ".join(segment.text for segment in self.partial_segments).strip()

    @property
    def final_text(self) -> str:
        return " ".join(segment.text for segment in self.final_segments).strip()

    @property
    def carry_over_text(self) -> str:
        return " ".join(segment.text for segment in self.carry_over_segments).strip()

    @property
    def should_clear_partial(self) -> bool:
        return not self.partial_segments


@dataclass
class StreamingCommitState:
    committed_segments: list[HypothesisSegment] = field(default_factory=list)
    previous_hypothesis: list[HypothesisSegment] = field(default_factory=list)
    pending_hypothesis: list[HypothesisSegment] = field(default_factory=list)
    carry_over_segments: list[HypothesisSegment] = field(default_factory=list)
    carry_over_text: str = ""
    agreement_candidate_text: str = ""
    agreement_pass_count: int = 0
    last_committed_end_ms: int = 0
    recent_voiced_ms: int = 0
    recent_unvoiced_ms: int = 0
    agreement_mode: str = "text_prefix"


def build_hypothesis_segments(
    segments: list[dict[str, int | str]],
    *,
    last_committed_end_ms: int,
    committed_segments: list[HypothesisSegment],
) -> list[HypothesisSegment]:
    normalized: list[HypothesisSegment] = []
    for raw in segments:
        text = _normalize_text(str(raw.get("text", "")))
        if not text:
            continue

        start_ms = max(0, int(raw.get("start_ms", 0)))
        end_ms = max(start_ms, int(raw.get("end_ms", start_ms)))
        if end_ms <= last_committed_end_ms:
            continue
        normalized.append(HypothesisSegment(start_ms=start_ms, end_ms=end_ms, text=text))

    return _drop_committed_duplicates(normalized, last_committed_end_ms, committed_segments)


def apply_decode_result(
    state: StreamingCommitState,
    segments: list[HypothesisSegment],
    *,
    force_finalize: bool,
    agreement_passes: int,
) -> CommitUpdate:
    previous_pending = list(state.previous_hypothesis)
    rolled_segments, carry_over_segments = _roll_pending_forward(previous_pending, segments)
    state.pending_hypothesis = list(rolled_segments)
    state.carry_over_segments = list(carry_over_segments)
    state.carry_over_text = " ".join(segment.text for segment in carry_over_segments).strip()

    if force_finalize:
        final_segments = rolled_segments or list(previous_pending)
        return _commit_segments(
            state,
            final_segments,
            partial_segments=[],
            carry_over_segments=carry_over_segments,
        )

    if not rolled_segments:
        state.previous_hypothesis = []
        state.pending_hypothesis = []
        state.carry_over_segments = []
        state.carry_over_text = ""
        state.agreement_candidate_text = ""
        state.agreement_pass_count = 0
        return CommitUpdate()

    if not previous_pending:
        state.previous_hypothesis = list(rolled_segments)
        state.agreement_candidate_text = ""
        state.agreement_pass_count = 0
        return CommitUpdate(partial_segments=list(rolled_segments), carry_over_segments=list(carry_over_segments))

    common_prefix_tokens = _longest_common_prefix_token_count(previous_pending, rolled_segments)
    if common_prefix_tokens <= 0:
        state.previous_hypothesis = list(rolled_segments)
        state.agreement_candidate_text = ""
        state.agreement_pass_count = 0
        return CommitUpdate(partial_segments=list(rolled_segments), carry_over_segments=list(carry_over_segments))

    common_prefix = _slice_segments_by_token_range(rolled_segments, 0, common_prefix_tokens)
    candidate_text = _canonical_text(common_prefix)
    if candidate_text == state.agreement_candidate_text:
        state.agreement_pass_count += 1
    else:
        state.agreement_candidate_text = candidate_text
        state.agreement_pass_count = 2

    if state.agreement_pass_count < max(1, agreement_passes):
        state.previous_hypothesis = list(rolled_segments)
        return CommitUpdate(partial_segments=list(rolled_segments), carry_over_segments=list(carry_over_segments))

    tail = _slice_segments_by_token_range(
        rolled_segments,
        common_prefix_tokens,
        _canonical_token_count(rolled_segments),
    )
    return _commit_segments(
        state,
        common_prefix,
        partial_segments=tail,
        carry_over_segments=carry_over_segments,
    )


def _commit_segments(
    state: StreamingCommitState,
    final_segments: list[HypothesisSegment],
    *,
    partial_segments: list[HypothesisSegment],
    carry_over_segments: list[HypothesisSegment],
) -> CommitUpdate:
    committed = _drop_already_committed_prefix(final_segments, state.committed_segments)
    if committed:
        state.committed_segments.extend(committed)
        state.last_committed_end_ms = max(state.last_committed_end_ms, committed[-1].end_ms)

    state.previous_hypothesis = list(partial_segments)
    state.pending_hypothesis = list(partial_segments)
    state.carry_over_segments = []
    state.carry_over_text = ""
    state.agreement_candidate_text = ""
    state.agreement_pass_count = 0
    return CommitUpdate(
        final_segments=committed,
        partial_segments=list(partial_segments),
        carry_over_segments=list(carry_over_segments),
        agreement_mode=state.agreement_mode,
    )


def _drop_committed_duplicates(
    segments: list[HypothesisSegment],
    last_committed_end_ms: int,
    committed_segments: list[HypothesisSegment],
) -> list[HypothesisSegment]:
    deduped = list(segments)
    while deduped:
        first = deduped[0]
        if first.end_ms <= last_committed_end_ms + 120:
            deduped.pop(0)
            continue

        if committed_segments and first.stable_text == committed_segments[-1].stable_text:
            deduped.pop(0)
            continue

        break

    return deduped


@dataclass(frozen=True)
class _WordPiece:
    display: str
    canonical_tokens: tuple[str, ...]
    start_ms: int
    end_ms: int
    source_segment: int


def _roll_pending_forward(
    previous_pending: list[HypothesisSegment],
    current_segments: list[HypothesisSegment],
) -> tuple[list[HypothesisSegment], list[HypothesisSegment]]:
    if not previous_pending or not current_segments:
        return list(current_segments), []

    prefix_overlap = _longest_common_prefix_token_count(previous_pending, current_segments)
    shift_overlap = _suffix_prefix_overlap_token_count(previous_pending, current_segments, min_tokens=3)
    if prefix_overlap >= shift_overlap and prefix_overlap > 0:
        return list(current_segments), []

    if shift_overlap > 0:
        previous_token_count = _canonical_token_count(previous_pending)
        carry_over = _slice_segments_by_token_range(previous_pending, 0, previous_token_count - shift_overlap)
        return [*carry_over, *current_segments], carry_over

    return list(current_segments), []


def _drop_already_committed_prefix(
    new_segments: list[HypothesisSegment],
    committed_segments: list[HypothesisSegment],
) -> list[HypothesisSegment]:
    if not new_segments or not committed_segments:
        return list(new_segments)

    overlap_tokens = _suffix_prefix_overlap_token_count(committed_segments, new_segments, min_tokens=1)
    if overlap_tokens <= 0:
        return list(new_segments)
    return _slice_segments_by_token_range(new_segments, overlap_tokens, _canonical_token_count(new_segments))


def _longest_common_prefix_token_count(
    previous: list[HypothesisSegment],
    current: list[HypothesisSegment],
) -> int:
    left = _canonical_tokens(previous)
    right = _canonical_tokens(current)
    count = 0
    for a, b in zip(left, right):
        if a != b:
            break
        count += 1
    return count


def _suffix_prefix_overlap_token_count(
    left_segments: list[HypothesisSegment],
    right_segments: list[HypothesisSegment],
    *,
    min_tokens: int,
) -> int:
    left = _canonical_tokens(left_segments)
    right = _canonical_tokens(right_segments)
    max_overlap = min(len(left), len(right))
    for overlap in range(max_overlap, min_tokens - 1, -1):
        if left[-overlap:] == right[:overlap]:
            return overlap
    return 0


def _canonical_tokens(segments: list[HypothesisSegment]) -> list[str]:
    tokens: list[str] = []
    for word in _segment_word_pieces(segments):
        tokens.extend(word.canonical_tokens)
    return tokens


def _canonical_token_count(segments: list[HypothesisSegment]) -> int:
    return len(_canonical_tokens(segments))


def _canonical_text(segments: list[HypothesisSegment]) -> str:
    return " ".join(_canonical_tokens(segments))


def _slice_segments_by_token_range(
    segments: list[HypothesisSegment],
    start_token: int,
    end_token: int,
) -> list[HypothesisSegment]:
    if end_token <= start_token:
        return []

    words = _segment_word_pieces(segments)
    if not words:
        return []

    start_word = _word_index_for_token_offset(words, start_token)
    end_word = _word_index_for_token_offset(words, end_token)
    selected_words = words[start_word:end_word]
    if not selected_words:
        return []

    grouped: list[HypothesisSegment] = []
    current_segment = selected_words[0].source_segment
    current_words: list[_WordPiece] = []
    for word in selected_words:
        if current_words and word.source_segment != current_segment:
            grouped.append(_build_segment_from_words(current_words))
            current_words = []
            current_segment = word.source_segment
        current_words.append(word)

    if current_words:
        grouped.append(_build_segment_from_words(current_words))
    return grouped


def _build_segment_from_words(words: list[_WordPiece]) -> HypothesisSegment:
    return HypothesisSegment(
        start_ms=words[0].start_ms,
        end_ms=words[-1].end_ms,
        text=" ".join(word.display for word in words).strip(),
    )


def _word_index_for_token_offset(words: list[_WordPiece], token_offset: int) -> int:
    if token_offset <= 0:
        return 0

    seen = 0
    for index, word in enumerate(words):
        canonical_count = len(word.canonical_tokens)
        if canonical_count <= 0:
            continue
        next_seen = seen + canonical_count
        if token_offset <= next_seen:
            return index + 1
        seen = next_seen
    return len(words)


def _segment_word_pieces(segments: list[HypothesisSegment]) -> list[_WordPiece]:
    words: list[_WordPiece] = []
    for segment_index, segment in enumerate(segments):
        for token in segment.stable_text.split(" "):
            display = token.strip()
            if not display:
                continue
            canonical_tokens = tuple(_TOKEN_RE.findall(display.lower().replace("-", " ")))
            words.append(
                _WordPiece(
                    display=display,
                    canonical_tokens=canonical_tokens,
                    start_ms=segment.start_ms,
                    end_ms=segment.end_ms,
                    source_segment=segment_index,
                )
            )
    return words


def _normalize_text(text: str) -> str:
    collapsed = _WHITESPACE_RE.sub(" ", text.strip())
    return collapsed
