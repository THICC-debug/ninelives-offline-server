"""Small pure-Python AES-256-CBC with PKCS7 padding (no installs needed).

Matches what the game uses: RijndaelManaged, 256-bit key, 128-bit block, CBC, PKCS7.
"""

_SBOX = [0] * 256
_INV = [0] * 256


def _init_sbox():
    p = q = 1
    while True:
        # multiply p by 3
        p = p ^ ((p << 1) & 0xFF) ^ (0x1B if p & 0x80 else 0)
        # divide q by 3
        q ^= q << 1
        q ^= q << 2
        q ^= q << 4
        q &= 0xFF
        if q & 0x80:
            q ^= 0x09
        x = q ^ ((q << 1) | (q >> 7)) & 0xFF ^ ((q << 2) | (q >> 6)) & 0xFF \
            ^ ((q << 3) | (q >> 5)) & 0xFF ^ ((q << 4) | (q >> 4)) & 0xFF
        x = (x ^ 0x63) & 0xFF
        _SBOX[p] = x
        _INV[x] = p
        if p == 1:
            break
    _SBOX[0] = 0x63
    _INV[0x63] = 0


_init_sbox()


def _xt(a):
    return ((a << 1) ^ 0x1B) & 0xFF if a & 0x80 else a << 1


def _mul(a, b):
    r = 0
    while b:
        if b & 1:
            r ^= a
        a = _xt(a)
        b >>= 1
    return r


_M2 = [_mul(i, 2) for i in range(256)]
_M3 = [_mul(i, 3) for i in range(256)]
_M9 = [_mul(i, 9) for i in range(256)]
_M11 = [_mul(i, 11) for i in range(256)]
_M13 = [_mul(i, 13) for i in range(256)]
_M14 = [_mul(i, 14) for i in range(256)]


def _expand(key):
    assert len(key) == 32
    nk, nr = 8, 14
    w = [list(key[i:i + 4]) for i in range(0, 32, 4)]
    rcon = 1
    for i in range(nk, 4 * (nr + 1)):
        t = list(w[i - 1])
        if i % nk == 0:
            t = t[1:] + t[:1]
            t = [_SBOX[b] for b in t]
            t[0] ^= rcon
            rcon = _xt(rcon)
        elif i % nk == 4:
            t = [_SBOX[b] for b in t]
        w.append([w[i - nk][j] ^ t[j] for j in range(4)])
    return [sum(w[r * 4:r * 4 + 4], []) for r in range(nr + 1)]


def _enc_block(rk, s):
    s = [s[i] ^ rk[0][i] for i in range(16)]
    for r in range(1, 15):
        s = [_SBOX[b] for b in s]
        # shift rows (column-major state)
        s = [s[0], s[5], s[10], s[15], s[4], s[9], s[14], s[3],
             s[8], s[13], s[2], s[7], s[12], s[1], s[6], s[11]]
        if r != 14:
            n = []
            for c in range(4):
                a0, a1, a2, a3 = s[c * 4:c * 4 + 4]
                n += [_M2[a0] ^ _M3[a1] ^ a2 ^ a3,
                      a0 ^ _M2[a1] ^ _M3[a2] ^ a3,
                      a0 ^ a1 ^ _M2[a2] ^ _M3[a3],
                      _M3[a0] ^ a1 ^ a2 ^ _M2[a3]]
            s = n
        k = rk[r]
        s = [s[i] ^ k[i] for i in range(16)]
    return s


def _dec_block(rk, s):
    s = [s[i] ^ rk[14][i] for i in range(16)]
    for r in range(13, -1, -1):
        s = [s[0], s[13], s[10], s[7], s[4], s[1], s[14], s[11],
             s[8], s[5], s[2], s[15], s[12], s[9], s[6], s[3]]
        s = [_INV[b] for b in s]
        k = rk[r]
        s = [s[i] ^ k[i] for i in range(16)]
        if r != 0:
            n = []
            for c in range(4):
                a0, a1, a2, a3 = s[c * 4:c * 4 + 4]
                n += [_M14[a0] ^ _M11[a1] ^ _M13[a2] ^ _M9[a3],
                      _M9[a0] ^ _M14[a1] ^ _M11[a2] ^ _M13[a3],
                      _M13[a0] ^ _M9[a1] ^ _M14[a2] ^ _M11[a3],
                      _M11[a0] ^ _M13[a1] ^ _M9[a2] ^ _M14[a3]]
            s = n
    return s


def encrypt_cbc(key: bytes, iv: bytes, data: bytes) -> bytes:
    rk = _expand(key)
    pad = 16 - len(data) % 16
    data = data + bytes([pad]) * pad
    prev = list(iv)
    out = bytearray()
    for i in range(0, len(data), 16):
        blk = [data[i + j] ^ prev[j] for j in range(16)]
        prev = _enc_block(rk, blk)
        out += bytes(prev)
    return bytes(out)


def decrypt_cbc(key: bytes, iv: bytes, data: bytes) -> bytes:
    rk = _expand(key)
    prev = list(iv)
    out = bytearray()
    for i in range(0, len(data), 16):
        blk = list(data[i:i + 16])
        d = _dec_block(rk, blk)
        out += bytes(d[j] ^ prev[j] for j in range(16))
        prev = blk
    pad = out[-1] if out else 0
    if 1 <= pad <= 16:
        out = out[:-pad]
    return bytes(out)
