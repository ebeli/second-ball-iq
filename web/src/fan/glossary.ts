import type { CandidateView, InsightKind, PredictionKind } from '../types';

/**
 * Learn the Game: football explained in a sentence or three, in English and Finnish. Written by hand, not by a
 * model, so a beginner always gets the same correct explanation. Each concept can be shown as a small diagram.
 */
export type Diagram =
  | 'goal' | 'xg' | 'possession' | 'finalThird' | 'press' | 'regain' | 'counter' | 'corner' | 'freeKick' | 'penalty'
  | 'formation' | 'falseNine' | 'invertedWinger' | 'wingBack' | 'shadowStriker' | 'secondStriker' | 'lowBlock'
  | 'direct' | 'substitution' | 'passing' | 'shape' | 'heat' | 'pitchControl' | 'threat';

export interface Concept {
  id: string;
  diagram: Diagram;
  en: { term: string; short: string; long: string };
  fi: { term: string; short: string; long: string };
}

const C = (id: string, diagram: Diagram, en: Concept['en'], fi: Concept['fi']): Concept => ({ id, diagram, en, fi });

export const concepts: Concept[] = [
  C('basics', 'goal',
    { term: 'How football works', short: 'Two teams of eleven try to get the ball into the other team\'s goal.',
      long: 'Two teams of eleven players try to put the ball into the other team\'s goal; only the goalkeeper may use their hands. A match has two halves of 45 minutes, plus a few minutes of stoppage time. The team with more goals wins; equal goals is a draw.' },
    { term: 'Näin jalkapallo toimii', short: 'Kaksi yhdentoista pelaajan joukkuetta yrittää saada pallon toisen maaliin.',
      long: 'Kaksi yhdentoista pelaajan joukkuetta yrittää saada pallon vastustajan maaliin; vain maalivahti saa käyttää käsiään. Ottelussa on kaksi 45 minuutin puoliaikaa ja muutama minuutti lisäaikaa. Enemmän maaleja tehnyt voittaa; tasapelissä maaleja on yhtä paljon.' }),
  C('xg', 'xg',
    { term: 'Expected goals (xG)', short: 'How likely a shot was to go in, from 0 to 1.',
      long: 'xG gives every shot a value between 0 and 1: how often a shot from that spot goes in. A shot from close in front of goal might be 0.4; a long-range effort 0.03. Adding them up shows how many good chances a team really had.' },
    { term: 'Odotettu maalimäärä (xG)', short: 'Kuinka todennäköisesti laukaus menee maaliin, 0–1.',
      long: 'xG antaa jokaiselle laukaukselle arvon 0:n ja 1:n väliltä: kuinka usein samasta paikasta laukaistu pallo menee maaliin. Läheltä maalin edestä laukaus voi olla 0,4, kaukolaukaus 0,03. Yhteenlaskettuna se kertoo, kuinka hyviä paikkoja joukkueella oikeasti oli.' }),
  C('possession', 'possession',
    { term: 'Possession', short: 'How much of the time a team has the ball.',
      long: 'Possession is the share of the ball a team has, counted here from completed passes. Having the ball a lot is not the same as being dangerous: a team can pass safely in midfield for minutes without getting near the goal.' },
    { term: 'Pallonhallinta', short: 'Kuinka suuren osan ajasta joukkue pitää palloa.',
      long: 'Pallonhallinta on joukkueen osuus pallosta, tässä laskettuna onnistuneista syötöistä. Paljon palloa ei tarkoita vaarallisuutta: joukkue voi syötellä turvallisesti keskikentällä minuuttikaupalla pääsemättä lähellekään maalia.' }),
  C('finalThird', 'finalThird',
    { term: 'Final third', short: 'The last third of the pitch, nearest the goal a team attacks.',
      long: 'The pitch can be split into three thirds. The final third is the one nearest the goal a team is attacking; most chances come from getting the ball there. A pass that carries the ball into it is a final-third entry.' },
    { term: 'Hyökkäyskolmannes', short: 'Kentän viimeinen kolmannes, lähinnä hyökkäyssuunnan maalia.',
      long: 'Kenttä voidaan jakaa kolmeen osaan. Hyökkäyskolmannes on lähinnä sitä maalia, jota kohti joukkue hyökkää; useimmat maalipaikat syntyvät, kun pallo saadaan sinne.' }),
  C('press', 'press',
    { term: 'Pressing', short: 'Chasing the opponent to win the ball back quickly.',
      long: 'Pressing means closing down the player with the ball to win it back. A high press does it near the opponent\'s goal, so a win there is close to a scoring chance. It is tiring, and it leaves space behind the players who press.' },
    { term: 'Prässi', short: 'Vastustajan jahtaaminen pallon voittamiseksi nopeasti takaisin.',
      long: 'Prässissä pallollista pelaajaa ahdistetaan, jotta pallo voitetaan takaisin. Korkea prässi tehdään lähellä vastustajan maalia, joten voitettu pallo on lähellä maalipaikkaa. Se on raskasta ja jättää tilaa prässäävien pelaajien taakse.' }),
  C('regain', 'regain',
    { term: 'High regain', short: 'Winning the ball back near the opponent\'s goal.',
      long: 'A high regain (or high turnover) is winning the ball in the third of the pitch nearest the opponent\'s goal. The opponent\'s defenders are often out of position at that moment, so it is one of the best ways to create a chance.' },
    { term: 'Korkea riisto', short: 'Pallon voittaminen lähellä vastustajan maalia.',
      long: 'Korkea riisto tarkoittaa pallon voittamista kentän siinä kolmanneksessa, joka on lähinnä vastustajan maalia. Vastustajan puolustajat ovat silloin usein väärissä paikoissa, joten se on yksi parhaista tavoista luoda maalipaikka.' }),
  C('counter', 'counter',
    { term: 'Quick attack after winning the ball', short: 'Attacking straight away before the other team can get back.',
      long: 'When a team wins the ball, the other team is still spread out for attacking. Going forward immediately, with few passes, catches them before they get back into shape. This is often called a counter-attack.' },
    { term: 'Nopea hyökkäys riiston jälkeen', short: 'Hyökkäys heti, ennen kuin vastustaja ehtii takaisin.',
      long: 'Kun joukkue voittaa pallon, vastustaja on vielä levittäytynyt hyökkäämään. Kun hyökätään heti muutamalla syötöllä, vastustaja ei ehdi järjestäytyä. Tätä kutsutaan usein vastahyökkäykseksi.' }),
  C('passing', 'passing',
    { term: 'Pass accuracy', short: 'The share of a player\'s passes that reach a teammate.',
      long: 'Pass accuracy is how many passes reached a teammate out of all the passes tried. Safe sideways passes push it up; risky forward passes pull it down, so a lower number is not always worse.' },
    { term: 'Syöttötarkkuus', short: 'Osuus syötöistä, jotka menevät joukkuetoverille.',
      long: 'Syöttötarkkuus kertoo, kuinka moni yritetyistä syötöistä meni perille. Varmat sivuttaissyötöt nostavat sitä ja riskialttiit eteenpäin suuntautuvat syötöt laskevat, joten pienempi luku ei aina ole huonompi.' }),
  C('corner', 'corner',
    { term: 'Corner kick', short: 'A free kick from the corner after the defenders put the ball behind their own goal line.',
      long: 'If a defending player last touches the ball before it goes over their own goal line, the attackers get a corner. The ball is kicked from the corner flag into the box, aimed at the near post, the far post or the middle, or played short to a teammate.' },
    { term: 'Kulmapotku', short: 'Potku kulmasta, kun puolustaja on vienyt pallon oman päätyrajansa yli.',
      long: 'Jos puolustaja koskettaa palloa viimeisenä ennen kuin se menee oman päätyrajan yli, hyökkääjät saavat kulmapotkun. Pallo potkaistaan kulmalipulta rangaistusalueelle lähitolpalle, takatolpalle tai keskelle, tai syötetään lyhyesti.' }),
  C('freeKick', 'freeKick',
    { term: 'Free kick', short: 'A kick awarded after a foul, taken from where the foul happened.',
      long: 'After a foul the other team restarts with a free kick from that spot, and opponents must stand at least 9.15 m away. Close to goal, a good taker can shoot directly over or around the defensive wall.' },
    { term: 'Vapaapotku', short: 'Rikkeen jälkeen tuomittu potku rikepaikalta.',
      long: 'Rikkeen jälkeen toinen joukkue jatkaa peliä vapaapotkulla rikepaikalta, ja vastustajien on oltava vähintään 9,15 metrin päässä. Lähellä maalia hyvä potkaisija voi laukoa suoraan muurin yli tai ohi.' }),
  C('penalty', 'penalty',
    { term: 'Penalty', short: 'A shot from 11 m with only the goalkeeper to beat, after a foul in the box.',
      long: 'A foul by a defender inside their own penalty area gives the attackers a penalty: one shot from the spot 11 metres out, with only the goalkeeper in the way. Most penalties go in, so the keeper often has to guess a side.' },
    { term: 'Rangaistuspotku', short: 'Laukaus 11 metristä vain maalivahtia vastaan rikkeen jälkeen.',
      long: 'Puolustajan rike omalla rangaistusalueella antaa hyökkääjille rangaistuspotkun: yksi laukaus 11 metrin pisteeltä, vain maalivahti edessä. Useimmat menevät sisään, joten maalivahti joutuu usein arvaamaan puolen.' }),
  C('formation', 'formation',
    { term: 'Formation', short: 'How a team lines up: defenders, midfielders and forwards, e.g. 4-3-3.',
      long: 'A formation counts players from the back: 4-3-3 is four defenders, three midfielders and three forwards (the goalkeeper is not counted). Coaches change it to defend better, or to get more players forward when chasing a goal.' },
    { term: 'Pelimuoto', short: 'Miten joukkue asettuu: puolustajat, keskikenttä ja hyökkääjät, esim. 4-3-3.',
      long: 'Pelimuoto lasketaan takaa: 4-3-3 tarkoittaa neljää puolustajaa, kolmea keskikenttäpelaajaa ja kolmea hyökkääjää (maalivahtia ei lasketa). Valmentaja muuttaa sitä puolustaakseen paremmin tai saadakseen enemmän pelaajia eteen maalia tavoitellessa.' }),
  C('falseNine', 'falseNine',
    { term: 'False nine', short: 'A centre-forward who drops back into midfield instead of staying up front.',
      long: 'The number 9 usually stays near the other team\'s defenders. A false nine drops back into midfield: the defenders must either follow and leave a gap behind them, or let the false nine have the ball in space.' },
    { term: 'Valeysi', short: 'Keskushyökkääjä, joka tulee keskikentälle sen sijaan, että pysyisi kärjessä.',
      long: 'Ysi pysyy yleensä vastustajan puolustajien tuntumassa. Valeysi tulee keskikentälle: puolustajien on joko seurattava ja jätettävä aukko taakseen tai annettava valeysin saada pallo vapaana.' }),
  C('invertedWinger', 'invertedWinger',
    { term: 'Inverted winger', short: 'A winger who cuts inside from the flank instead of staying wide.',
      long: 'A traditional winger runs down the touchline and crosses. An inverted winger starts wide but comes inside towards goal, usually to shoot with the stronger foot. It also leaves the flank free for the full-back to run into.' },
    { term: 'Sisäänpäin hakeutuva laituri', short: 'Laituri, joka tulee laidalta keskelle sen sijaan, että pysyisi leveänä.',
      long: 'Perinteinen laituri juoksee sivurajaa pitkin ja keskittää. Sisäänpäin hakeutuva laituri lähtee laidalta mutta tulee kohti maalia, yleensä laukoakseen vahvemmalla jalallaan. Samalla laita jää laitapuolustajalle vapaaksi.' }),
  C('wingBack', 'wingBack',
    { term: 'Wing-back', short: 'A wide player who covers the whole flank, defending and attacking.',
      long: 'With three centre-backs, the two wide players become wing-backs: they defend on the flank and also push up as high as the midfield and beyond. It is one of the most demanding roles on the pitch.' },
    { term: 'Laitatukimies', short: 'Laitapelaaja, joka kattaa koko laidan puolustaen ja hyökäten.',
      long: 'Kun keskuspuolustajia on kolme, laitapelaajista tulee laitatukimiehiä: he puolustavat laidalla ja nousevat keskikentälle ja sen yli. Se on yksi kentän raskaimmista rooleista.' }),
  C('shadowStriker', 'shadowStriker',
    { term: 'Shadow striker', short: 'A midfielder who keeps arriving in the box from behind the forwards.',
      long: 'A shadow striker plays behind the forwards but runs into the penalty area late, when defenders are busy marking the strikers. Arriving from deep makes them hard to pick up.' },
    { term: 'Varjokärki', short: 'Keskikenttäpelaaja, joka saapuu jatkuvasti boksiin hyökkääjien takaa.',
      long: 'Varjokärki pelaa hyökkääjien takana mutta juoksee rangaistusalueelle myöhään, kun puolustajat vartioivat kärkiä. Takaa tuleva pelaaja on vaikea ottaa kiinni.' }),
  C('secondStriker', 'secondStriker',
    { term: 'Second striker', short: 'A forward playing just behind and alongside the main striker.',
      long: 'With two forwards, one often stays highest against the defenders while the second striker plays a little deeper, linking with midfield and running beyond the main striker. Adding one is a common way to chase a goal.' },
    { term: 'Toinen kärki', short: 'Hyökkääjä, joka pelaa pääkärjen vieressä ja hieman takana.',
      long: 'Kahden kärjen kanssa toinen pysyy korkeimmalla puolustajia vastaan ja toinen kärki pelaa hieman syvemmällä, yhdistää keskikentän ja juoksee pääkärjen ohi. Sen lisääminen on yleinen tapa tavoitella maalia.' }),
  C('lowBlock', 'lowBlock',
    { term: 'Sitting deep (low block)', short: 'Defending close to your own goal instead of pressing high.',
      long: 'A team protecting a lead often stops pressing up the pitch and waits in its own half in two compact lines. It leaves little space behind for the opponent, who gets the ball but finds it hard to get through.' },
    { term: 'Matala puolustus', short: 'Puolustaminen lähellä omaa maalia korkean prässin sijaan.',
      long: 'Johtoaan puolustava joukkue lopettaa usein prässin ja odottaa omalla puoliskollaan kahdessa tiiviissä linjassa. Vastustaja saa pallon, mutta sen taakse jää vähän tilaa ja läpi on vaikea päästä.' }),
  C('direct', 'direct',
    { term: 'Going direct', short: 'Playing long balls forward instead of passing through midfield.',
      long: 'A direct team kicks the ball long towards its forwards, skipping the midfield. It loses the ball more often, but it gets the ball near goal fast, which is why a team chasing a goal often does it.' },
    { term: 'Suoraviivainen peli', short: 'Pitkiä palloja eteen keskikentän kautta syöttelyn sijaan.',
      long: 'Suoraviivainen joukkue potkaisee pallon pitkälle hyökkääjille keskikentän yli. Pallo menetetään useammin, mutta se saadaan nopeasti maalin lähelle, ja siksi maalia jahtaava joukkue tekee usein niin.' }),
  C('substitution', 'substitution',
    { term: 'Substitution', short: 'A coach replaces a player on the pitch with one from the bench.',
      long: 'Coaches can replace players during the match. A player who comes off cannot return. Substitutions refresh tired legs, or change the plan: an extra striker to chase a goal, an extra defender to protect a lead.' },
    { term: 'Vaihto', short: 'Valmentaja vaihtaa kentällä olevan pelaajan penkiltä tulevaan.',
      long: 'Valmentaja voi vaihtaa pelaajia ottelun aikana. Kentältä poistunut pelaaja ei voi palata. Vaihdoilla tuodaan tuoreita jalkoja tai muutetaan suunnitelmaa: lisähyökkääjä maalia tavoitellessa, lisäpuolustaja johtoa puolustettaessa.' }),
  C('shape', 'shape',
    { term: 'Average positions', short: 'Where each player has been on the ball, on average.',
      long: 'Second Ball IQ averages where each player touched the ball over a spell of play. Together the dots show the team\'s real shape, which can differ from the team sheet: a forward who drops deep, a full-back who plays as high as a winger.' },
    { term: 'Keskimääräiset sijainnit', short: 'Missä kukin pelaaja on keskimäärin ollut pallossa.',
      long: 'Second Ball IQ laskee, missä kukin pelaaja on keskimäärin koskettanut palloa. Pisteet yhdessä näyttävät joukkueen todellisen muodon, joka voi poiketa kokoonpanosta: syvälle tuleva hyökkääjä tai laituriksi nouseva laitapuolustaja.' }),
  C('heat', 'heat',
    { term: 'Heat map', short: 'A map of where a team or player had the ball most.',
      long: 'The brighter an area on a heat map, the more often the team or player had the ball there. It shows at a glance which side a team attacks down and how high up the pitch it plays.' },
    { term: 'Lämpökartta', short: 'Kartta siitä, missä joukkue tai pelaaja oli eniten pallossa.',
      long: 'Mitä kirkkaampi alue lämpökartalla, sitä useammin joukkue tai pelaaja oli siellä pallossa. Siitä näkee yhdellä silmäyksellä, kumpaa laitaa joukkue hyökkää ja kuinka korkealla se pelaa.' }),
  C('pitchControl', 'pitchControl',
    { term: 'Pitch control', short: 'Which team would reach each part of the pitch first.',
      long: 'From tracking, Second Ball IQ works out for every spot on the pitch which team would get there first, from where every player is and which way he is running. A team can control the space near the other goal without having the ball, and that space is where chances come from.' },
    { term: 'Kenttähallinta', short: 'Kumpi joukkue ehtisi ensin mihinkin kohtaan kenttää.',
      long: 'Seurantadatasta Second Ball IQ laskee jokaiselle kentän kohdalle, kumpi joukkue ehtisi sinne ensin: missä kukin pelaaja on ja mihin suuntaan hän juoksee. Joukkue voi hallita tilaa vastustajan maalin edessä ilman palloa, ja siitä tilasta maalipaikat syntyvät.' }),
  C('threat', 'threat',
    { term: 'Expected threat (xT)', short: 'How likely a goal is from where the ball is.',
      long: 'Expected threat gives every part of the pitch a value: the chance of scoring in the next few moves when you have the ball there. A pass that moves the ball to a more dangerous spot adds threat. Second Ball IQ uses it to spot a better pass: a teammate in a more dangerous place who was free to find.' },
    { term: 'Odotettu uhka (xT)', short: 'Kuinka todennäköinen maali on siitä, missä pallo on.',
      long: 'Odotettu uhka antaa jokaiselle kentän kohdalle arvon: todennäköisyyden tehdä maali muutaman seuraavan siirron aikana, kun pallo on siellä. Syöttö vaarallisempaan paikkaan lisää uhkaa. Second Ball IQ käyttää sitä löytääkseen paremman syötön: vaarallisemmalla paikalla vapaana olleen joukkuetoverin.' }),
];

export const conceptById = Object.fromEntries(concepts.map((c) => [c.id, c])) as Record<string, Concept>;

/** The concepts a moment touches on, from its kind and the engine's tags: deterministic, so always right. */
export function conceptsFor(kind: InsightKind, c?: CandidateView): string[] {
  const t = c?.tags ?? {};
  switch (kind) {
    case 'pressingShift': return ['press', 'regain'];
    case 'possessionIllusion': return ['possession', 'finalThird'];
    case 'turnoverToChance': return ['regain', 'counter', 'xg'];
    case 'goal': return c?.facts.penalty ? ['penalty', 'xg'] : c?.facts.directFreeKick ? ['freeKick'] : ['xg'];
    case 'playerMilestone': return ['passing'];
    case 'tacticalShift': {
      const base = t.shift === 'formation' ? ['formation', 'shape'] : t.shift === 'deeper' ? ['lowBlock', 'press'] : ['direct'];
      return t.cause === 'substitution' ? [...base, 'substitution'] : base;
    }
    case 'territory': return ['pitchControl', 'finalThird'];
    case 'betterPass': return ['threat', 'passing'];
    case 'playerRole': {
      const role: Record<string, string> = { falseNine: 'falseNine', invertedWinger: 'invertedWinger', wingBack: 'wingBack', shadowStriker: 'shadowStriker', secondStriker: 'secondStriker' };
      return [role[t.role ?? ''] ?? 'shape', 'heat'].filter(Boolean);
    }
  }
  return [];
}

export function conceptsForPrediction(kind: PredictionKind): string[] {
  return kind === 'corner' ? ['corner'] : kind === 'penalty' ? ['penalty'] : kind === 'freeKick' ? ['freeKick'] : ['xg'];
}
