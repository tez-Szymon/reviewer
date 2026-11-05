import { db } from "./db";

export interface Setting {
  key: string;
  value: string;
}

export async function saveSetting(setting: Setting): Promise<void> {
  await db.put("settings", setting.key, setting.value);
}
