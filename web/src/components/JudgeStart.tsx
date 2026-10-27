import { Wordmark } from './Brand';
import { useEffect, useState } from 'react';
import { Button, Spinner } from '@fluentui/react-components';
import { api } from '../api';
import { TAKE } from '../take/autopilot';
import { TAKE_FAN_PREFS } from '../take/scripts';

/**
 * The judge link (/judge): starts the demo match at 38′, or joins it if it is already playing, then opens the fan view
 * in judge mode with a demo watch party. Nothing to configure.
 */
export function JudgeStart() {
  const [error, setError] = useState<string>();
  useEffect(() => {
    let cancelled = false;
    // A recording take (/judge?take=N) always starts the demo match afresh, from known settings, then plays itself.
    if (TAKE) {
      try {
        localStorage.setItem('matchlens-fan', JSON.stringify(TAKE_FAN_PREFS));
        localStorage.setItem('ml-cam', 'auto');
        localStorage.setItem('ml-stage3d', 'on');
        localStorage.setItem('ml-ticker', 'on');
      } catch { /* private mode */ }
    }
    api.judge(!!TAKE)
      .then(() => {
        if (cancelled) return;
        window.location.replace(TAKE === 2 ? '/?take=2&started=1' : TAKE ? `/fan?judge=1&take=${TAKE}` : '/fan?judge=1');
      })
      .catch((e: unknown) => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); });
    return () => { cancelled = true; };
  }, []);
  return (
    <main className="judge-start">
      <h1><Wordmark /></h1>
      {error ? (
        <>
          <p>The demo match did not start: {error}</p>
          <Button appearance="primary" onClick={() => window.location.reload()}>Try again</Button>
        </>
      ) : (
        <Spinner label="Starting the demo match from 38′…" />
      )}
    </main>
  );
}
