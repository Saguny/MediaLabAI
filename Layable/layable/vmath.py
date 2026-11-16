"""Small vector/quaternion helpers. Monado/OpenXR convention: right-handed,
+Y up, -Z forward, metres. Quaternions are (x, y, z, w)."""

import math

UP = (0.0, 1.0, 0.0)


def add(a, b):
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def mul(a, s):
    return (a[0] * s, a[1] * s, a[2] * s)


def dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def length(a):
    return math.sqrt(dot(a, a))


def norm(a, fallback=(0.0, 0.0, -1.0)):
    n = length(a)
    return mul(a, 1.0 / n) if n > 1e-6 else fallback


def lerp(a, b, t):
    return add(a, mul(sub(b, a), t))


def horiz(a):
    return (a[0], 0.0, a[2])


def reject(a, axis):
    """Component of a perpendicular to unit vector axis."""
    return sub(a, mul(axis, dot(a, axis)))


def rotate(q, v):
    x, y, z, w = q
    # v' = v + 2w(u x v) + 2 u x (u x v)
    u = (x, y, z)
    t = mul(cross(u, v), 2.0)
    return add(add(v, mul(t, w)), cross(u, t))


def axis_angle(axis, angle):
    s = math.sin(angle / 2)
    return (axis[0] * s, axis[1] * s, axis[2] * s, math.cos(angle / 2))


def rotate_about(v, axis, angle):
    return rotate(axis_angle(axis, angle), v)


def signed_angle(a, b, axis):
    """Angle from a to b around axis (a, b roughly perpendicular to axis)."""
    return math.atan2(dot(cross(a, b), axis), dot(a, b))


def basis_quat(forward, up):
    """Orientation whose -Z is `forward` and +Y is (orthogonalised) `up`."""
    f = norm(forward)
    u = norm(reject(up, f), fallback=norm(reject(UP, f), fallback=(0.0, 0.0, 1.0)))
    r = cross(f, u)
    z = mul(f, -1.0)
    # rotation matrix columns: X=r, Y=u, Z=z
    m00, m01, m02 = r[0], u[0], z[0]
    m10, m11, m12 = r[1], u[1], z[1]
    m20, m21, m22 = r[2], u[2], z[2]
    tr = m00 + m11 + m22
    if tr > 0:
        s = math.sqrt(tr + 1.0) * 2
        q = ((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25 * s)
    elif m00 > m11 and m00 > m22:
        s = math.sqrt(1.0 + m00 - m11 - m22) * 2
        q = (0.25 * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s)
    elif m11 > m22:
        s = math.sqrt(1.0 + m11 - m00 - m22) * 2
        q = ((m01 + m10) / s, 0.25 * s, (m12 + m21) / s, (m02 - m20) / s)
    else:
        s = math.sqrt(1.0 + m22 - m00 - m11) * 2
        q = ((m02 + m20) / s, (m12 + m21) / s, 0.25 * s, (m10 - m01) / s)
    return qnorm(q)


def qnorm(q):
    n = math.sqrt(sum(c * c for c in q)) or 1.0
    return tuple(c / n for c in q)


def nlerp(a, b, t):
    if sum(x * y for x, y in zip(a, b)) < 0:
        b = tuple(-c for c in b)
    return qnorm(tuple(x + (y - x) * t for x, y in zip(a, b)))


def from_to(a, b):
    """Shortest rotation taking direction a to direction b."""
    a, b = norm(a, fallback=None), norm(b, fallback=None)
    if a is None or b is None:
        return (0.0, 0.0, 0.0, 1.0)
    c = dot(a, b)
    if c < -0.999999:
        axis = norm(cross(a, (1.0, 0.0, 0.0)), fallback=None) or norm(cross(a, (0.0, 1.0, 0.0)))
        return (axis[0], axis[1], axis[2], 0.0)
    x = cross(a, b)
    return qnorm((x[0], x[1], x[2], 1.0 + c))


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)
