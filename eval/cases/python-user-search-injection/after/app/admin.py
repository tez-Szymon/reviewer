from flask import Blueprint, jsonify, request

from app.users import find_user, search_users

admin = Blueprint("admin", __name__)


@admin.get("/admin/users/<int:user_id>")
def user(user_id: int):
    found = find_user(user_id)
    return (jsonify(dict(found)), 200) if found else ("", 404)


@admin.get("/admin/users")
def search():
    return jsonify([dict(row) for row in search_users(request.args.get("q", ""))])
