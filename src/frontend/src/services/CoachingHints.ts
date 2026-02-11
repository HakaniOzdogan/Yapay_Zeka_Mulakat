import { Metrics } from './MetricsComputer';

export interface CoachingHint {
  type: 'warning' | 'info' | 'good';
  message: string;
}

export function generateCoachingHints(metrics: Metrics): CoachingHint[] {
  const hints: CoachingHint[] = [];

  if (metrics.eyeContact < 40) {
    hints.push({
      type: 'warning',
      message: 'Kameraya daha cok bakin.'
    });
  } else if (metrics.eyeContact >= 70) {
    hints.push({
      type: 'good',
      message: 'Harika goz iletisimi.'
    });
  }

  if (metrics.headStability < 40) {
    hints.push({
      type: 'warning',
      message: 'Basinizi daha sabit tutun.'
    });
  }

  if (metrics.posture < 40) {
    hints.push({
      type: 'warning',
      message: 'Posturunuzu duzeltin.'
    });
  } else if (metrics.posture >= 70) {
    hints.push({
      type: 'good',
      message: 'Postur iyi gorunuyor.'
    });
  }

  if (metrics.fidget < 40) {
    hints.push({
      type: 'warning',
      message: 'El hareketlerini azaltin.'
    });
  }

  if (metrics.eyeOpenness < 30) {
    hints.push({
      type: 'info',
      message: 'Gozleriniz sik kapaniyor, odaga geri donun.'
    });
  }

  if (metrics.emotion === 'Tense' || metrics.emotion === 'Angry') {
    hints.push({
      type: 'info',
      message: 'Yuz ifadesi gergin gorunuyor, nefesinizi yavaslatin.'
    });
  }

  if (metrics.emotion === 'Sad' || metrics.emotion === 'LowEnergy') {
    hints.push({
      type: 'info',
      message: 'Enerjiyi biraz yukseltip daha canli bir ton kullanin.'
    });
  }

  if (metrics.emotion === 'Happy') {
    hints.push({
      type: 'good',
      message: 'Pozitif ifade iyi bir ilk izlenim veriyor.'
    });
  }

  return hints;
}
