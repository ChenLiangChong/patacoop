#!/usr/bin/env python3
"""jevbot [--games 0,1] [--decider 0] [--seconds 900] [--eval] : Jev decides every drum command.

The in-game driver (scratchpad jevdriver.cs) writes the battle as it stands to tmp/jev<i>.json.
This program turns that into a short, plain description of the screen (what is ahead and how
close, whether the army moves, its health, the messages on screen, the drums unlocked), asks Jev
which drum command to play, and writes it to tmp/jev<i>.cmd; the driver plays it. Code does no
deciding of its own: it only describes and executes. A low-confidence answer keeps the drum that
is playing. --eval runs labelled situations instead, to check the decisions before playing.
The API key is read from ~/.config/typesafe/jev.key. Decisions are logged to tmp/jevbot.log.
"""
import argparse, json, os, re, time, urllib.request

def work_dir():
    """The Windows-side work folder (tools/env.sh exports it; otherwise %USERPROFILE%\\PataCoop)."""
    if os.environ.get('PATACOOP_WORK'):
        return os.environ['PATACOOP_WORK']
    import subprocess
    profile = subprocess.run(['cmd.exe', '/c', 'echo %USERPROFILE%'], cwd='/mnt/c', stdin=subprocess.DEVNULL, capture_output=True, text=True).stdout.strip()
    return subprocess.run(['wslpath', profile], stdin=subprocess.DEVNULL, capture_output=True, text=True).stdout.strip() + '/PataCoop'


TMP = work_dir() + '/tmp'
API = 'https://api.typesafe.ai/v1/systemone'
KEY = open(os.path.expanduser('~/.config/typesafe/jev.key')).read().strip()
KEEP_BELOW = 0.45  # confidence under which the playing drum stays

# drum command -> (setAutoCommand id, Jev criterion)
COMMANDS = {
    'march': (1, {'drums': 'PATA PATA PATA PON',
                  'what': 'The army walks forward.',
                  'when': 'No enemy and no obstacle is right next to or just ahead of our army: the screen shows only scenery, allies, plants, magic seals, the goal marker, or things further ahead.',
                  'examples': ['Nothing but scenery on screen', 'Only the goal marker ahead', 'A magic seal and a friendly Patapon next to the army, no enemy', 'The screen says PATA PATA PATA PON']}),
    'attack': (3, {'drums': 'PON PON PATA PON',
                   'what': 'The army attacks what is in front of it.',
                   'when': 'An enemy or an obstacle (wall, rock wall, pillar, gate, enemy building) is right next to or just ahead of our army.',
                   'examples': ['A stone pillar right next to the army', 'Enemy soldiers just ahead', 'A boss just ahead while our health bars hold', 'The screen says PON PON PATA PON']}),
    'defend': (2, {'drums': 'CHAKA CHAKA PATA PON',
                   'what': 'The army raises shields and holds its ground.',
                   'when': 'The screen asks for CHAKA CHAKA PATA PON, or our health bars are dropping while a boss or strong enemies are next to our army.',
                   'examples': ['The screen shows CHAKA CHAKA PATA PON', 'A boss next to the army, our health bars dropping, a unit just fell']}),
    'charge': (5, {'drums': 'PON PON CHAKA CHAKA',
                   'what': 'The army gathers power so its next attack is stronger.',
                   'when': 'The screen asks for PON PON CHAKA CHAKA.',
                   'examples': ['The screen shows PON PON CHAKA CHAKA']}),
    'retreat': (4, {'drums': 'PON PATA PON PATA',
                    'what': 'The army jumps backwards out of danger.',
                    'when': 'The screen asks for PON PATA PON PATA, or our health bars are low and units keep falling.',
                    'examples': ['The screen shows PON PATA PON PATA', 'Our health bars low, units falling one after another']}),
    'jump': (8, {'drums': 'DON DON CHAKA CHAKA',
                 'what': 'The army jumps.',
                 'when': 'The screen asks for DON DON CHAKA CHAKA.',
                 'examples': ['The screen shows DON DON CHAKA CHAKA']}),
    'miracle': (6, {'drums': 'DON DON-DON DON-DON',
                    'what': 'The army calls a miracle such as rain or wind.',
                    'when': 'The screen asks for DON DON-DON DON-DON or for a miracle.',
                    'examples': ['The screen shows DON DON-DON DON-DON']}),
}
NAME_BY_ID = {v[0]: k for k, v in COMMANDS.items()}

QUESTIONS = {
    'drum_command': {
        'type': 'choice',
        'instructions': {
            'question': 'Which drum command should the player play now?',
            'game': 'Patapon 2: a rhythm game seen from the side. Our army walks to the right and only acts on drum commands. Obstacles block the way until the army breaks them; allies, plants and magic seals are left alone.',
            'look_at': ['`messages_on_screen`', '`things_on_screen`', '`our_army`'],
            'focus': 'Play what the screen tells the player to drum. Otherwise protect the army when its health bars drop, attack enemies and obstacles next to or just ahead of it, and march when nothing is in the way.',
        },
        'criteria': {name: crit for name, (_, crit) in COMMANDS.items()},
    },
    'screen_requests_drums': {
        'type': 'noul',
        'instructions': {
            'question': 'Do `messages_on_screen` tell the player to play a specific drum sequence right now?',
            'focus': 'A tutorial or prompt naming drums such as PATA, PON, CHAKA or DON.',
        },
        'criteria': {
            'true': {'what': 'A message names drums for the player to play', 'examples': ['CHAKA CHAKA PATA PON 請依序並按照節奏', 'Press PON PON PATA PON']},
            'false': {'what': 'The messages are story talk, unit shouts or empty', 'examples': ['衝鋒～！', '偉大的神明 再會了！', '']},
        },
    },
}

# what a gimmick is, from words in its layout name (general, not per mission)
KINDS = [
    (('GOAL',), 'goal marker', 'goal'),
    (('SEAL',), 'magic seal', 'story'),
    (('HEROPATAPON', 'PATAPON', 'TATEPON', 'YARIPON', 'YUMIPON', 'HATAPON', 'ANGEL', 'GOD'), 'friendly Patapon', 'ally'),
    (('ROCKFACE', 'ROCK'), 'rock wall', 'obstacle'),
    (('IRONFACE', 'IRON'), 'iron wall', 'obstacle'),
    (('STELA',), 'stone pillar', 'obstacle'),
    (('WAREHOUSE', 'HOUSE', 'TOWER', 'CASTLE', 'FORT', 'HUT'), 'enemy building', 'obstacle'),
    (('DOOR', 'GATE'), 'gate', 'obstacle'),
    (('WALL', 'FENCE', 'BARRICADE'), 'wall', 'obstacle'),
    (('FLAG',), 'enemy flag', 'obstacle'),
    # chests stand in the way until broken open
    (('BOX', 'CHEST', 'PRIZE'), 'treasure chest (break it open)', 'obstacle'),
    (('GRASS', 'BUSH', 'FLOWER', 'TREE', 'PLANT'), 'plant', 'scenery'),
    # signboards along hunting grounds have hit points but block nothing: marching walks past them
    (('BILLBOARD', 'SIGN', 'KANBAN'), 'signboard', 'scenery'),
]


def kind_of(name):
    for words, text, role in KINDS:
        if any(w in name for w in words):
            return text, role
    return name.lower(), 'unknown'


HALF_SCREEN = 277.0  # game units from the camera centre to the screen edge at the usual zoom (camera fov 1.007)


def bar(hp, mx):
    r = hp / mx if mx else 0
    return 'full' if r >= 0.95 else 'mostly full' if r >= 0.6 else 'about half' if r >= 0.3 else 'low' if r >= 0.1 else 'almost empty'


def clean(text):
    """A screen message without the game's format codes (&H20O1R255G255B255#...) and with its line breaks as spaces."""
    return re.sub(r'&[A-Z0-9]+#', '', text).replace('/', ' ').strip()


class History:
    """Recent snapshots, for what visibly changes: our health bars and the bars of what is in front of us."""
    def __init__(self):
        self.items = []

    def add(self, s):
        now = time.time()
        front = s.get('army_mid_x', s.get('army_front_x', 0))
        ahead_hp = sum(o['hp'] for o in s.get('objects_ahead', []) if -60 < o['x'] - front < 250) + \
                   sum(e.get('hp', 0) for e in s.get('enemies', []) if -60 < e['x'] - front < 250)
        self.items.append((now, s.get('army_hp_percent', 100), s.get('army_units', 0), ahead_hp))
        self.items = [i for i in self.items if now - i[0] < 20]

    def since(self, seconds):
        now = time.time()
        old = [i for i in self.items if now - i[0] >= seconds]
        return (old[-1] if old else self.items[0]), self.items[-1]


def describe(s, hist=None):
    """What a player sees on the screen right now, in words (Jev should not do arithmetic)."""
    cam = s.get('camera_x', s.get('army_front_x', 0) + 127)
    half = HALF_SCREEN * (s.get('camera_fov', 1.0070549) / 1.0070549)
    front = s.get('army_mid_x', s.get('army_front_x', s.get('army_x', 0)))  # where most of the army stands

    def place(x):
        r = (x - cam) / half
        side = 'left part of the screen' if r < -0.33 else 'middle of the screen' if r < 0.33 else 'right part of the screen'
        d = x - front
        near = 'right next to our army' if -60 <= d <= 60 else 'just ahead of our army' if 60 < d <= 200 else \
               'further ahead of our army' if d > 200 else 'behind our army'
        return side, near

    def visible(x):
        return abs(x - cam) <= half + 30

    things = []
    for o in s.get('objects_ahead', []):
        if not visible(o['x']):
            continue
        text, role = kind_of(o['name'])
        side, near = place(o['x'])
        things.append({'what': text, 'kind': role, 'where': side, 'position': near, 'health_bar': bar(o['hp'], o.get('max_hp', o['hp']))})
    for e in s.get('enemies', []):
        if e.get('hp', 1) <= 0 or not visible(e['x']):  # a fallen enemy lies on the ground, it is no longer a foe
            continue
        side, near = place(e['x'])
        things.append({'what': 'boss monster' if e.get('max_hp', 0) >= 2000 else 'enemy soldier', 'kind': 'enemy',
                       'where': side, 'position': near, 'health_bar': bar(e.get('hp', 0), e.get('max_hp', 0))})
    army = {'walking': s.get('seconds_since_army_moved', 0) < 3,
            'squad_health_bars': [bar(q['hp'], q['max_hp']) for q in s.get('our_squads', [])]}
    if hist and hist.items:
        (_, hp0, units0, ahead0), (_, hp1, units1, ahead1) = hist.since(4)
        army['our_health_bars_dropping'] = hp1 < hp0 - 2 or units1 < units0
        army['a_unit_just_fell'] = units1 < units0
        if ahead0 or ahead1:
            army['health_bars_in_front_dropping'] = ahead1 < ahead0
    # what matters first, as a player's eye goes: enemies, then what blocks the way; grass is background
    order = {'enemy': 0, 'obstacle': 1, 'goal': 2, 'story': 3, 'ally': 4, 'loot': 5, 'unknown': 6}
    things = sorted((t for t in things if t['kind'] != 'scenery'), key=lambda t: order.get(t['kind'], 9))
    return {
        'messages_on_screen': [clean(t) for t in s.get('recent_screen_text', []) if clean(t)],
        'things_on_screen': things or 'nothing but scenery',
        'our_army': army,
    }


def ask(state):
    body = {'model': 'jev-latest', 'state': state, 'questions': QUESTIONS}
    req = urllib.request.Request(API, data=json.dumps(body).encode(), method='POST',
                                 headers={'Authorization': 'Bearer ' + KEY, 'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=10) as r:
        return json.load(r)['answers']


# labelled situations from this game, checked before playing (--eval)
def at(raw, front=1000):
    """A labelled situation placed on the screen: army front at `front`, the camera where it usually sits."""
    raw = dict(raw)
    raw.setdefault('army_front_x', front); raw.setdefault('army_x', front - 90); raw.setdefault('camera_x', front + 127)
    raw.setdefault('camera_fov', 1.0070549); raw.setdefault('our_squads', [{'units': 3, 'hp': 300, 'max_hp': 300}])
    for key in ('objects_ahead', 'enemies'):
        raw[key] = [dict(t, x=front + t['distance_ahead']) for t in raw.get(key, [])]
    return raw


HURT = [{'units': 1, 'hp': 40, 'max_hp': 130}, {'units': 1, 'hp': 30, 'max_hp': 140}]
EVAL = [  # (right answer, driver state, visible changes)
    ('attack', {'objects_ahead': [{'name': 'STELA', 'distance_ahead': 25, 'hp': 50, 'max_hp': 50}], 'seconds_since_army_moved': 60}, {}),
    ('attack', {'objects_ahead': [{'name': 'ROCKFACE_HI_000', 'distance_ahead': 120, 'hp': 300, 'max_hp': 300}], 'seconds_since_army_moved': 10}, {}),
    ('attack', {'enemies': [{'distance_ahead': 90, 'kind': 'unit004_03_01', 'hp': 80, 'max_hp': 80}, {'distance_ahead': 140, 'kind': 'unit002_03_01', 'hp': 70, 'max_hp': 70}], 'seconds_since_army_moved': 4}, {}),
    ('march', {'enemies': [{'distance_ahead': 1279, 'kind': 'unit201_01_01', 'hp': 6000, 'max_hp': 6000}], 'seconds_since_army_moved': 0}, {}),
    ('march', {'seconds_since_army_moved': 5}, {}),
    ('defend', {'seconds_since_army_moved': 30, 'recent_screen_text': ['沒弄錯太鼓的聲音嗎？', 'CHAKA CHAKA PATA PON', '請依序並按照節奏♪']}, {}),
    ('march', {'seconds_since_army_moved': 0, 'recent_screen_text': ['衝鋒～！', '行軍♪']}, {}),
    ('attack', {'objects_ahead': [{'name': 'WAREHOUSE', 'distance_ahead': 40, 'hp': 50, 'max_hp': 50}, {'name': 'DOOR_BS_000_1', 'distance_ahead': 40, 'hp': 1, 'max_hp': 1}], 'seconds_since_army_moved': 8}, {}),
    # the seal-rock boss scene that went wrong: a boss tearing into the army next to a story seal
    ('defend', {'objects_ahead': [{'name': 'SEALROCK_000', 'distance_ahead': 104, 'hp': 49986, 'max_hp': 50000}, {'name': 'HEROPATAPON_000', 'distance_ahead': 104, 'hp': 1000, 'max_hp': 1000}],
                'enemies': [{'distance_ahead': 150, 'kind': 'unit201_01_01', 'hp': 6000, 'max_hp': 6000}], 'our_squads': HURT, 'seconds_since_army_moved': 60},
     {'our_health_bars_dropping': True, 'a_unit_just_fell': True, 'health_bars_in_front_dropping': False}),
    ('march', {'objects_ahead': [{'name': 'SEALROCK_000', 'distance_ahead': 104, 'hp': 50000, 'max_hp': 50000}, {'name': 'HEROPATAPON_000', 'distance_ahead': 104, 'hp': 1000, 'max_hp': 1000}],
               'seconds_since_army_moved': 20}, {'our_health_bars_dropping': False, 'a_unit_just_fell': False, 'health_bars_in_front_dropping': False}),
    ('attack', {'enemies': [{'distance_ahead': 120, 'kind': 'unit201_01_01', 'hp': 5200, 'max_hp': 6000}], 'seconds_since_army_moved': 10},
     {'our_health_bars_dropping': False, 'a_unit_just_fell': False, 'health_bars_in_front_dropping': True}),
    # the hero mission: the boss far off screen, the army idle
    ('march', {'enemies': [{'distance_ahead': 1704, 'kind': 'unit201_01_01', 'hp': 2915, 'max_hp': 6000}], 'seconds_since_army_moved': 180,
               'our_squads': [{'units': 1, 'hp': 118, 'max_hp': 130}, {'units': 1, 'hp': 107, 'max_hp': 140}]}, {'our_health_bars_dropping': False, 'a_unit_just_fell': False}),
    ('defend', {'enemies': [{'distance_ahead': 150, 'kind': 'unit201_01_01', 'hp': 2915, 'max_hp': 6000}], 'our_squads': HURT, 'seconds_since_army_moved': 8},
     {'our_health_bars_dropping': True, 'a_unit_just_fell': True, 'health_bars_in_front_dropping': False}),
    # the boss just fell (its body still lies there) and the goal waits ahead
    ('march', {'enemies': [{'distance_ahead': 121, 'kind': 'unit201_01_01', 'hp': 0, 'max_hp': 6000}],
               'objects_ahead': [{'name': 'MULTI_MISSION_GOAL', 'distance_ahead': 252, 'hp': 1, 'max_hp': 1}], 'seconds_since_army_moved': 86}, {}),
]


def evaluate():
    right = 0
    for want, raw, trends in EVAL:
        state = describe(at(raw))
        state['our_army'].update(trends)
        a = ask(state)
        got = a['drum_command']['choice']; conf = a['drum_command'].get('confidence', 0)
        right += got == want
        print(f"{'OK ' if got == want else 'BAD'} want {want:7} got {got:7} conf {conf:.2f}  {json.dumps(a['drum_command'].get('probabilities', {}))}")
    print(f'{right}/{len(EVAL)} right')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--games', default='0')
    ap.add_argument('--decider', type=int, default=0)
    ap.add_argument('--seconds', type=float, default=900)
    ap.add_argument('--every', type=float, default=0.7)
    ap.add_argument('--eval', action='store_true')
    args = ap.parse_args()
    if args.eval:
        return evaluate()
    games = [int(g) for g in args.games.split(',')]
    log = open(os.path.join(TMP, 'jevbot.log'), 'a', encoding='utf-8')
    seq, current, end, idle_since, hist = int(time.time()), None, time.time() + args.seconds, None, History()
    while time.time() < end:
        time.sleep(args.every)
        try:
            raw = json.load(open(os.path.join(TMP, f'jev{args.decider}.json'), encoding='utf-8'))
        except (OSError, ValueError):
            continue
        if not raw.get('playing'):
            idle_since = idle_since or time.time()
            if time.time() - idle_since > 30 and current is not None:
                print(time.strftime('%T'), 'battle over', flush=True); break
            continue
        idle_since = None
        hist.add(raw)
        state = describe(raw, hist)
        try:
            a = ask(state)
        except Exception as e:
            log.write(f"{time.strftime('%T')} jev unavailable: {e}\n"); log.flush()
            continue
        choice, conf = a['drum_command']['choice'], a['drum_command'].get('confidence', 0)
        keep = conf < KEEP_BELOW and current is not None
        seen = state['things_on_screen'] if isinstance(state['things_on_screen'], str) else [(t['what'], t['position'], t['health_bar']) for t in state['things_on_screen']][:3]
        line = (f"{time.strftime('%T')} x={raw.get('army_x')} screen={seen} walking={state['our_army']['walking']} "
                f"msgs={state['messages_on_screen'][:2]} -> {choice} {conf:.2f}"
                f"{' (kept ' + current + ')' if keep else ''}")
        log.write(line + '\n'); log.flush()
        if keep or choice == current:
            continue
        current, seq = choice, seq + 1
        for g in games:
            with open(os.path.join(TMP, f'jev{g}.cmd'), 'w') as f:
                f.write(f'{seq} {COMMANDS[choice][0]}')
        print(line, flush=True)


if __name__ == '__main__':
    main()
