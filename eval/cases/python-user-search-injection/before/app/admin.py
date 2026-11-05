from flask import Blueprint, jsonify

from app.users import find_user

admin = Blueprint("admin", __name__)


@admin.get("/admin/users/<int:user_id>")
def user(user_id: int):
    found = find_user(user_id)
    return (jsonify(dict(found)), 200) if found else ("", 404)
