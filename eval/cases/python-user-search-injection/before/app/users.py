from app.db import connection


def find_user(user_id: int):
    """One user by id, or None."""
    with connection() as conn:
        cur = conn.execute("SELECT id, name, email FROM users WHERE id = ?", (user_id,))
        return cur.fetchone()
