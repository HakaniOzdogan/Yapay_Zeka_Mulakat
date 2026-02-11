import React, { useState } from 'react';

interface TranscriptModalProps {
  isOpen: boolean;
  transcript: string;
  segments: any[];
  stats: {
    duration_ms: number;
    word_count: number;
    wpm: number;
    filler_count: number;
    filler_words: string[];
    pause_count: number;
    average_pause_ms: number;
  } | null;
  onClose: () => void;
}

export const TranscriptModal: React.FC<TranscriptModalProps> = ({
  isOpen,
  transcript,
  segments,
  stats,
  onClose
}) => {
  if (!isOpen) return null;

  const durationSec = (stats?.duration_ms || 0) / 1000;

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal-content" onClick={(e) => e.stopPropagation()}>
        <div className="modal-header">
          <h2>Transcript & Analysis</h2>
          <button className="modal-close" onClick={onClose}>✕</button>
        </div>

        <div className="modal-body">
          <div className="transcript-section">
            <h3>Full Transcript</h3>
            <div className="transcript-text">
              {transcript || '(No transcript available)'}
            </div>
          </div>

          {stats && (
            <div className="stats-section">
              <h3>Speech Statistics</h3>
              <div className="stats-grid">
                <div className="stat-item">
                  <div className="stat-label">Duration</div>
                  <div className="stat-value">{durationSec.toFixed(1)}s</div>
                </div>
                <div className="stat-item">
                  <div className="stat-label">Words</div>
                  <div className="stat-value">{stats.word_count}</div>
                </div>
                <div className="stat-item">
                  <div className="stat-label">WPM</div>
                  <div className="stat-value">{stats.wpm.toFixed(0)}</div>
                </div>
                <div className="stat-item">
                  <div className="stat-label">Filler Words</div>
                  <div className="stat-value">{stats.filler_count}</div>
                </div>
                <div className="stat-item">
                  <div className="stat-label">Pauses</div>
                  <div className="stat-value">{stats.pause_count}</div>
                </div>
                <div className="stat-item">
                  <div className="stat-label">Avg Pause</div>
                  <div className="stat-value">
                    {stats.average_pause_ms ? `${stats.average_pause_ms.toFixed(0)}ms` : '-'}
                  </div>
                </div>
              </div>

              {stats.filler_words && stats.filler_words.length > 0 && (
                <div className="filler-section">
                  <p><strong>Filler Words Detected:</strong> {stats.filler_words.join(', ')}</p>
                </div>
              )}
            </div>
          )}

          {segments && segments.length > 0 && (
            <div className="segments-section">
              <h3>Transcript Segments</h3>
              <div className="segments-list">
                {segments.map((seg, idx) => (
                  <div key={idx} className="segment">
                    <span className="segment-time">
                      {(seg.start_ms / 1000).toFixed(1)}s - {(seg.end_ms / 1000).toFixed(1)}s
                    </span>
                    <span className="segment-text">{seg.text}</span>
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>

        <div className="modal-footer">
          <button onClick={onClose} className="btn btn-primary">
            Close
          </button>
        </div>
      </div>
    </div>
  );
};
