import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { FluentProvider } from '@fluentui/react-components';
import { App } from './App';
import { FanApp } from './FanApp';
import { JudgeStart } from './components/JudgeStart';
import { Overlay } from './Overlay';
import { arenaTheme } from './theme';
import '@fontsource/big-shoulders-display/700';
import '@fontsource/big-shoulders-display/800';
import '@fontsource/barlow/400';
import '@fontsource/barlow/500';
import '@fontsource/barlow/600';
import '@fontsource/barlow-condensed/600';
import '@fontsource/barlow-condensed/700';
import './styles.css';
import './arena.css';

// The arena look is dark only: a night fixture under floodlights. The fonts ship with the app (no font CDN at the venue).
document.documentElement.dataset.theme = 'dark';

function Root() {
  const path = window.location.pathname;
  // The broadcast overlay is graphics only: no theme provider painting a background behind them.
  if (path.startsWith('/overlay')) return <Overlay />;
  const fan = path.startsWith('/fan');
  return (
    <FluentProvider theme={arenaTheme} className="ml-root ml-dark arena">
      {path.startsWith('/judge') ? <JudgeStart /> : fan ? <FanApp /> : <App />}
    </FluentProvider>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Root />
  </StrictMode>,
);
