import { useState } from 'react'

interface TranscriptSegment {
  startMs: number
  endMs: number
  text: string
  questionOrder?: number
}

interface TranscriptModalProps {
  isOpen: boolean
  onClose: () => void
  transcript: string
  segments?: TranscriptSegment[]
  questionCount?: number
}

function formatTimestamp(ms: number): string {
  const totalSeconds = Math.floor(ms / 1000)
  const minutes = Math.floor(totalSeconds / 60)
  const seconds = totalSeconds % 60
  return `${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`
}

export function TranscriptModal({ isOpen, onClose, transcript, segments, questionCount }: TranscriptModalProps) {
  const [viewMode, setViewMode] = useState<'full' | 'segments'>('full')
  const [copiedState, setCopiedState] = useState(false)

  if (!isOpen) return null

  const hasSegments = segments && segments.length > 0

  const handleCopy = async () => {
    const textToCopy = hasSegments && viewMode === 'segments'
      ? segments.map(s => `[${formatTimestamp(s.startMs)}] ${s.text}`).join('\n')
      : transcript

    try {
      await navigator.clipboard.writeText(textToCopy)
      setCopiedState(true)
      setTimeout(() => setCopiedState(false), 2000)
    } catch {
      // Clipboard API may not be available
    }
  }

  const groupedByQuestion = hasSegments
    ? segments.reduce<Record<number, TranscriptSegment[]>>((acc, seg) => {
        const q = seg.questionOrder ?? 0
        if (!acc[q]) acc[q] = []
        acc[q].push(seg)
        return acc
      }, {})
    : {}

  return (
    <div className="transcript-modal-overlay" onClick={onClose}>
      <div className="transcript-modal" onClick={(e) => e.stopPropagation()}>
        <div className="transcript-modal-header">
          <h2>📄 Interview Transcript</h2>
          <div className="transcript-modal-actions">
            {hasSegments && (
              <div className="transcript-view-toggle">
                <button
                  type="button"
                  className={`btn btn-sm ${viewMode === 'full' ? 'btn-primary' : 'btn-secondary'}`}
                  onClick={() => setViewMode('full')}
                >
                  Full Text
                </button>
                <button
                  type="button"
                  className={`btn btn-sm ${viewMode === 'segments' ? 'btn-primary' : 'btn-secondary'}`}
                  onClick={() => setViewMode('segments')}
                >
                  By Question
                </button>
              </div>
            )}
            <button type="button" className="btn btn-sm btn-secondary" onClick={handleCopy}>
              {copiedState ? '✅ Copied' : '📋 Copy'}
            </button>
            <button type="button" className="transcript-modal-close" onClick={onClose}>✕</button>
          </div>
        </div>

        <div className="transcript-modal-body">
          {viewMode === 'full' || !hasSegments ? (
            <div className="transcript-full-text">
              {transcript || 'No transcript data available.'}
            </div>
          ) : (
            <div className="transcript-segments">
              {Object.entries(groupedByQuestion)
                .sort(([a], [b]) => Number(a) - Number(b))
                .map(([questionOrder, segs]) => (
                  <div key={questionOrder} className="transcript-question-group">
                    <div className="transcript-question-label">
                      {Number(questionOrder) > 0 ? `Question ${questionOrder}` : 'General'}
                    </div>
                    {segs.map((seg, idx) => (
                      <div key={idx} className="transcript-segment-row">
                        <span className="transcript-timestamp">{formatTimestamp(seg.startMs)}</span>
                        <span className="transcript-text">{seg.text}</span>
                      </div>
                    ))}
                  </div>
                ))}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
