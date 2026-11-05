from app.db import connection


def find_user(user_id: int):
    """One user by id, or None."""
    with connection() as conn:
        cur = conn.execute("SELECT id, name, email FROM users WHERE id = ?", (user_id,))
        return cur.fetchone()


def search_users(name: str, limit: int = 20):
    """Users whose name contains `name`, for the admin search box."""
    with connection() as conn:
        cur = conn.execute(
            f"SELECT id, name, email FROM users WHERE name LIKE '%{name}%' ORDER BY name LIMIT ?",
            (limit,),
        )
        return cur.fetchall()
