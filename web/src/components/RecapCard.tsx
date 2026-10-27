import { clock } from '../format';
import type { MatchInfo, RecapView } from '../types';

/** A half-time or full-time recap: headline, the story in a few paragraphs, and the moments that shaped it. */
export function RecapCard({ recap, info, fi, onListen }: { recap: RecapView; info: MatchInfo; fi?: boolean; onListen?: () => void }) {
  const label = recap.fullTime ? (fi ? 'Lopputulos' : 'Full time') : (fi ? 'Puoliaika' : 'Half time');
  return (
    <article className="recap">
      <header className="recap-head">
        <span className="recap-when">{label}</span>
        {onListen && (
          <button type="button" className="listen" onClick={onListen} aria-label={fi ? 'Kuuntele yhteenveto' : 'Listen to the recap'}>
            <SpeakerIcon /> {fi ? 'Kuuntele' : 'Listen'}
          </button>
        )}
        <span className="recap-score">
          {info.home.shortName} {recap.homeGoals}–{recap.awayGoals} {info.away.shortName}
        </span>
      </header>
      <h3 className="recap-headline">{recap.headline}</h3>
      {recap.paragraphs.map((p, i) => <p key={i} className="recap-p">{p}</p>)}
      {recap.keyMoments.length > 0 && (
        <ol className="recap-moments" aria-label={fi ? 'Ratkaisevat hetket' : 'Key moments'}>
          {recap.keyMoments.map((k) => (
            <li key={k.insightId} className={k.teamId === info.home.teamId ? 'side-home' : 'side-away'}>
              <span className="rm-clock">{clock(k.clockSec)}</span>
              <span>{k.text}</span>
            </li>
          ))}
        </ol>
      )}
      <p className="recap-by">
        {recap.fromTemplate
          ? (fi ? 'Koottu julkaistuista hetkistä' : 'Assembled from the published moments')
          : (fi ? 'Kirjoittanut Recap Writer -agentti julkaistujen hetkien pohjalta' : 'Written by the Recap Writer agent from the published moments')}
      </p>
    </article>
  );
}

export function SpeakerIcon() {
  return (
    <svg viewBox="0 0 20 20" width="14" height="14" aria-hidden="true">
      <path d="M3 8h3l4-3.5v11L6 12H3z" fill="currentColor" />
      <path d="M13 7a4 4 0 0 1 0 6M15.2 4.8a7 7 0 0 1 0 10.4" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
    </svg>
  );
}
