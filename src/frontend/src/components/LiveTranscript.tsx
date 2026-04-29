import React, { useState, useEffect, useRef } from 'react'

interface LiveTranscriptProps {
  finalSegments: string[]
  partialText: string
  isConnected: boolean
}

export const LiveTranscript: React.FC<LiveTranscriptProps> = ({
  finalSegments,
  partialText,
  isConnected
}) => {
  const containerRef = useRef<HTMLDivElement>(null)
  const [prevWordCount, setPrevWordCount] = useState(0)
  const [newWordIndices, setNewWordIndices] = useState<Set<number>>(new Set())

  // Combine final and partial text
  const finalText = finalSegments.join(' ')
  const combinedText = finalText + (finalText && partialText ? ' ' : '') + partialText
  const words = combinedText.split(/\s+/).filter(w => w.length > 0)

  // Detect new words and apply animation
  useEffect(() => {
    if (words.length > prevWordCount) {
      const newIndices = new Set<number>()
      for (let i = prevWordCount; i < words.length; i++) {
        newIndices.add(i)
      }
      setNewWordIndices(newIndices)

      // Remove animation class after animation completes
      const timer = setTimeout(() => {
        setNewWordIndices(new Set())
      }, 120)

      return () => clearTimeout(timer)
    } else if (words.length < prevWordCount) {
      setNewWordIndices(new Set())
    }

    setPrevWordCount(words.length)
  }, [words, prevWordCount])

  // Auto-scroll to bottom
  useEffect(() => {
    if (containerRef.current) {
      containerRef.current.scrollTop = containerRef.current.scrollHeight
    }
  }, [words])

  // Determine which words belong to partial
  const finalWords = finalText.split(/\s+/).filter(w => w.length > 0)
  const partialWords = partialText.split(/\s+/).filter(w => w.length > 0)
  const partialStartIndex = finalWords.length

  return (
    <div
      ref={containerRef}
      className="live-transcript-content"
      aria-live="polite"
      aria-atomic="false"
    >
      <p className="live-transcript-paragraph">
        {words.map((word, idx) => {
          const isPartial = idx >= partialStartIndex
          const isNew = newWordIndices.has(idx)
          const classNames = [
            'live-transcript-word',
            isNew && 'word-new',
            isPartial && 'word-partial'
          ]
            .filter(Boolean)
            .join(' ')

          return (
            <span key={idx} className={classNames}>
              {word}
            </span>
          )
        })}

        {isConnected && partialText.length > 0 && (
          <span className="transcript-cursor" aria-hidden="true" />
        )}
      </p>
    </div>
  )
}
