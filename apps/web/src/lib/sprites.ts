// ADR-0016: original pixel art, drawn here as grids of palette keys ('.' is transparent). No external assets.
// The avatar is deliberately neutral: two dot eyes, no mouth, no expression.
export const PALETTE: Record<string, string> = {
  k: '#2b2d42', // outline
  w: '#f8f9fa', // white
  g: '#adb5bd', // grey
  b: '#748ffc', // avatar blue
  y: '#f59f00', // coin gold
  l: '#ffd43b', // coin light
  r: '#e03131', // refusal red
  n: '#8d6e63', // wood
  o: '#e8590c', // roof
  p: '#e9ecef', // wall and paper
  t: '#2f9e44', // green
  s: '#74c0fc', // window
};

export const SPRITES: Record<string, string[]> = {
  avatar: [
    '..kkkk..',
    '.kbbbbk.',
    'kbwbbwbk',
    'kbbbbbbk',
    '.kbbbbk.',
    '..kggk..',
    '.kggggk.',
    'kggggggk',
    'kgkggkgk',
    '..kggk..',
    '..kk.kk.',
    '..kk.kk.',
  ],
  home: [
    '.....kk.....',
    '....kook....',
    '...kooook...',
    '..kooooook..',
    '.kooooooook.',
    'kkkkkkkkkkkk',
    'kppppppppppk',
    'kpkkpppkkkpk',
    'kpkskppknkpk',
    'kpkkpppknkpk',
    'kppppppknkpk',
    'kkkkkkkkkkkk',
  ],
  mailbox: [
    '.kkkkkk.',
    'kssssssk',
    'kskkkksk',
    'kssssssk',
    '.kkkkkk.',
    '...kk...',
    '...kk...',
    '...kk...',
  ],
  counter: [
    'kkkkkkkkkkkk',
    'kggggggggggk',
    'kgkkkkkkkkgk',
    'kgk......kgk',
    'kgkkkkkkkkgk',
    'knnnnnnnnnnk',
    'knnnnnnnnnnk',
    'kkkkkkkkkkkk',
  ],
  vault: [
    'kkkkkkkkkk',
    'kggggggggk',
    'kgkkkkkkgk',
    'kgkyyyykgk',
    'kgkykkykgk',
    'kgkyyyykgk',
    'kgkkkkkkgk',
    'kggggggggk',
    'kkkkkkkkkk',
  ],
  postbox: [
    '.kkkkkk.',
    'krrrrrrk',
    'krkkkkrk',
    'krrrrrrk',
    'krrrrrrk',
    'kkkkkkkk',
    '...kk...',
    '...kk...',
  ],
  desk: [
    'kkkkkkkkkk',
    'knnnnnnnnk',
    'kkkkkkkkkk',
    '.kn....nk.',
    '.kn....nk.',
    '.kk....kk.',
  ],
  stall: [
    'kkkkkkkkkk',
    'ktwtwtwtwk',
    'kkkkkkkkkk',
    '.kn....nk.',
    '.knppppnk.',
    '.knppppnk.',
    '.kkkkkkkk.',
  ],
  door: [
    'kkkkkk',
    'kggggk',
    'kggggk',
    'kgggyk',
    'kggggk',
    'kggggk',
    'kkkkkk',
  ],
  coin: [
    '.kkkk.',
    'kllllk',
    'klyylk',
    'klyylk',
    'kllllk',
    '.kkkk.',
  ],
  letter: [
    'kkkkkkkk',
    'kpkppkpk',
    'kppkkppk',
    'kppppppk',
    'kkkkkkkk',
  ],
  sun: [
    '..ll..',
    '.llll.',
    'llllll',
    'llllll',
    '.llll.',
    '..ll..',
  ],
};

export interface Rect { x: number; y: number; size: number; fill: string }

export function spriteRects(rows: readonly string[], size: number): Rect[] {
  const rects: Rect[] = [];
  rows.forEach((row, y) => [...row].forEach((key, x) => {
    if (key !== '.') rects.push({ x: x * size, y: y * size, size, fill: PALETTE[key] });
  }));
  return rects;
}
