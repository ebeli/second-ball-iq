import { useEffect, useState } from 'react';
import { createDarkTheme, createLightTheme, type BrandVariants, type Theme } from '@fluentui/react-components';

/**
 * Second Ball IQ on Fluent 2: a pitch-green brand ramp, navy neutrals in the dark theme. Green is for the pitch,
 * selected things and verified states; on-air yellow (our own token) stays reserved for what is on screen.
 */
const pitch: BrandVariants = {
  10: '#020805', 20: '#0b1f15', 30: '#0f3322', 40: '#12422c', 50: '#155236', 60: '#186341', 70: '#1a744c',
  80: '#1c8657', 90: '#2a9764', 100: '#3fa673', 110: '#57b583', 120: '#6fc394', 130: '#88d1a6', 140: '#a2deb9',
  150: '#bdeacc', 160: '#d9f5e1',
};

export const lightTheme: Theme = {
  ...createLightTheme(pitch),
  colorNeutralBackground2: '#f3f5f7',
  colorNeutralBackground3: '#eaeef1',
};

const dark = createDarkTheme(pitch);
export const darkTheme: Theme = {
  ...dark,
  colorBrandForeground1: pitch[110],
  colorBrandForeground2: pitch[120],
  colorBrandForegroundLink: pitch[120],
  colorBrandForegroundLinkHover: pitch[130],
  colorCompoundBrandForeground1: pitch[110],
  colorCompoundBrandStroke: pitch[110],
  colorNeutralBackground1: '#141f29',
  colorNeutralBackground1Hover: '#1a2734',
  colorNeutralBackground1Pressed: '#111a23',
  colorNeutralBackground1Selected: '#1c2a37',
  colorNeutralBackground2: '#0f1821',
  colorNeutralBackground3: '#0b131a',
  colorNeutralBackground4: '#09111a',
  colorSubtleBackgroundHover: '#1a2734',
  colorSubtleBackgroundPressed: '#111a23',
  colorSubtleBackgroundSelected: '#1c2a37',
  colorNeutralStroke1: '#33424f',
  colorNeutralStroke2: '#26333d',
  colorNeutralStroke3: '#1e2a33',
};

/**
 * The arena theme (dark only): night-pitch surfaces, chalk-green brand for active and verified states, 4px corners,
 * Barlow for text. Team colours come from the kits (kit.ts), never from the theme.
 */
const chalk: BrandVariants = {
  10: '#00140b', 20: '#002417', 30: '#003a25', 40: '#004e33', 50: '#006241', 60: '#00774f', 70: '#0b8c5f', 80: '#17a16f',
  90: '#26b680', 100: '#34cb91', 110: '#42e2a2', 120: '#68ffbd', 130: '#8affcb', 140: '#adffda', 150: '#cfffe8', 160: '#ecfff5',
};
const night = createDarkTheme(chalk);
export const arenaTheme: Theme = {
  ...night,
  fontFamilyBase: "Barlow, 'Segoe UI', system-ui, sans-serif",
  borderRadiusSmall: '2px', borderRadiusMedium: '4px', borderRadiusLarge: '4px', borderRadiusXLarge: '4px',
  colorBrandBackground: chalk[110], colorBrandBackgroundHover: chalk[120], colorBrandBackgroundPressed: chalk[100],
  colorBrandBackgroundSelected: chalk[110], colorNeutralForegroundOnBrand: '#06140d',
  colorBrandForeground1: chalk[110], colorBrandForeground2: chalk[120], colorBrandForegroundLink: chalk[120],
  colorBrandForegroundLinkHover: chalk[130], colorCompoundBrandForeground1: chalk[110], colorCompoundBrandStroke: chalk[110],
  colorCompoundBrandBackground: chalk[110], colorBrandStroke1: chalk[110],
  colorNeutralForeground1: '#dfe2eb', colorNeutralForeground2: '#bbc6cf', colorNeutralForeground3: '#8d9aa6',
  colorNeutralBackground1: '#0b1118', colorNeutralBackground1Hover: '#121b26', colorNeutralBackground1Pressed: '#0a0f15',
  colorNeutralBackground1Selected: '#131e2a', colorNeutralBackground2: '#0b1118', colorNeutralBackground3: '#080c12',
  colorNeutralBackground4: '#080c12', colorSubtleBackgroundHover: '#121b26', colorSubtleBackgroundPressed: '#0a0f15',
  colorSubtleBackgroundSelected: '#131e2a', colorNeutralStroke1: '#2d4538', colorNeutralStroke2: '#1f3027',
  colorNeutralStroke3: '#18241e', colorNeutralStrokeAccessible: '#85948a', colorTransparentStroke: 'transparent',
};

/** Follows the system light/dark setting, unless the page pins one with data-theme on <html>. */
export function useScheme(): 'light' | 'dark' {
  const read = () => {
    const pinned = document.documentElement.dataset.theme;
    if (pinned === 'light' || pinned === 'dark') return pinned;
    return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  };
  const [scheme, setScheme] = useState<'light' | 'dark'>(read);
  useEffect(() => {
    const m = window.matchMedia?.('(prefers-color-scheme: dark)');
    const on = () => setScheme(read());
    m?.addEventListener('change', on);
    return () => m?.removeEventListener('change', on);
  }, []);
  return scheme;
}

/** True on screens wide enough for the desktop layout (rail, side-by-side panes, side drawers). */
export function useWide(min = 900): boolean {
  const q = `(min-width: ${min}px)`;
  const [wide, setWide] = useState(() => window.matchMedia?.(q).matches ?? true);
  useEffect(() => {
    const m = window.matchMedia?.(q);
    const on = () => setWide(m.matches);
    m?.addEventListener('change', on);
    return () => m?.removeEventListener('change', on);
  }, [q]);
  return wide;
}
