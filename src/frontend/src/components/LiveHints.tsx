import React from 'react';
import { CoachingHint } from '../services/CoachingHints';
import { BehaviorStats } from '../services/MetricsComputer';

interface LiveHintsProps {
  hints: CoachingHint[];
  metrics: {
    eyeContact: number;
    headStability: number;
    posture: number;
    fidget: number;
    eyeOpenness: number;
    emotion: string;
  };
  behaviorStats: BehaviorStats;
}

export const LiveHints: React.FC<LiveHintsProps> = ({ hints, metrics, behaviorStats }) => {
  const emotionRows = Object.entries(behaviorStats.emotionPercentages)
    .sort((a, b) => b[1] - a[1]);

  return (
    <div className="live-hints">
      <div className="metrics-mini">
        <div className={`metric ${getMetricStatus(metrics.eyeContact)}`}>
          <div className="metric-value">{Math.round(metrics.eyeContact)}</div>
          <div className="metric-label">Göz İletişimi</div>
        </div>
        <div className={`metric ${getMetricStatus(metrics.headStability)}`}>
          <div className="metric-value">{Math.round(metrics.headStability)}</div>
          <div className="metric-label">Baş Stabilite</div>
        </div>
        <div className={`metric ${getMetricStatus(metrics.posture)}`}>
          <div className="metric-value">{Math.round(metrics.posture)}</div>
          <div className="metric-label">Postür</div>
        </div>
        <div className={`metric ${getMetricStatus(metrics.fidget)}`}>
          <div className="metric-value">{Math.round(metrics.fidget)}</div>
          <div className="metric-label">Dili Hareketleri</div>
        </div>
      </div>

      <div className="behavior-stats">
        <div className="behavior-card">
          <div className="behavior-title">Duygu Durumu</div>
          <div className="behavior-main">{behaviorStats.currentEmotion}</div>
          <div className="behavior-sub">Baskin: {behaviorStats.dominantEmotion}</div>
        </div>
        <div className="behavior-card">
          <div className="behavior-title">Goz Durumu</div>
          <div className="behavior-main">{behaviorStats.currentEyesOpen ? 'Acik' : 'Kapali'}</div>
          <div className="behavior-sub">Aciklik: {Math.round(metrics.eyeOpenness)}%</div>
        </div>
        <div className="behavior-card">
          <div className="behavior-title">Goz Istatistik</div>
          <div className="behavior-main">Blink: {behaviorStats.blinkCount}</div>
          <div className="behavior-sub">
            Acik/Kapali: {Math.round(behaviorStats.eyeOpenPercent)}% / {Math.round(behaviorStats.eyeClosedPercent)}%
          </div>
        </div>
      </div>

      <div className="emotion-breakdown">
        {emotionRows.map(([emotion, value]) => (
          <div key={emotion}>{toLabel(emotion)}: {Math.round(value)}%</div>
        ))}
      </div>

      <div className="coaching-hints">
        {hints.length === 0 ? (
          <div className="hint hint-good">✓ Mükemmel gidiyorsunuz!</div>
        ) : (
          hints.map((hint, idx) => (
            <div key={idx} className={`hint hint-${hint.type}`}>
              {hint.message}
            </div>
          ))
        )}
      </div>
    </div>
  );
};

function getMetricStatus(score: number): string {
  if (score >= 70) return 'good';
  if (score >= 40) return 'fair';
  return 'poor';
}

function toLabel(emotion: string): string {
  const labels: Record<string, string> = {
    Neutral: 'Nötr',
    Happy: 'Mutlu',
    Sad: 'Üzgün',
    Angry: 'Kızgın',
    Surprised: 'Şaşkın',
    Tense: 'Gergin',
    LowEnergy: 'Düşük Enerji'
  };
  return labels[emotion] ?? emotion;
}
