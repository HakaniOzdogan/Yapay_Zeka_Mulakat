"""
Filler word detection.
"""


# Turkish filler words (common hesitations, ums, etc.)
TURKISH_FILLERS = {
    "eee", "ııı", "şey", "yani", "aslında", "hmm", "eeeey", "hani", "falan", "filan",
    "yâni", "işte", "ee", "ii", "aa", "hım", "um", "uh", "ek", "so", "like", "you know"
}

# English fillers
ENGLISH_FILLERS = {
    "um", "uh", "er", "erm", "like", "you know", "actually", "basically", "literally",
    "so", "well", "i mean", "kind of", "sort of", "thing"
}

FILLER_WORDS_BY_LANG = {
    "tr": TURKISH_FILLERS,
    "en": ENGLISH_FILLERS,
}


def detect_filler_words(text: str, language: str = "tr") -> list[str]:
    """
    Detect filler words in text (simplified: word-level matching).
    
    Args:
        text: full transcript
        language: language code (tr, en)
        
    Returns:
        list of detected filler words
    """
    fillers = FILLER_WORDS_BY_LANG.get(language, TURKISH_FILLERS)
    
    # Simple tokenization: lowercase, split by whitespace/punctuation
    words = text.lower().split()
    detected = []
    
    for word in words:
        # Strip punctuation
        clean_word = word.strip('.,!?;:\'"')
        if clean_word in fillers:
            detected.append(clean_word)
    
    return detected


def count_filler_words(text: str, language: str = "tr") -> int:
    """Count occurrences of filler words"""
    return len(detect_filler_words(text, language))


def compute_filler_rate(
    word_count: int,
    filler_count: int,
    duration_seconds: float
) -> float:
    """
    Compute filler words per minute.
    
    Args:
        word_count: total words
        filler_count: number of filler words
        duration_seconds: duration in seconds
        
    Returns:
        filler words per minute
    """
    if duration_seconds == 0:
        return 0.0
    minutes = duration_seconds / 60
    if minutes == 0:
        return 0.0
    return filler_count / minutes
