import { db } from "./db";

export interface Setting {
  key: string;
  value: string;
}

export async function saveSetting(setting: Setting): Promise<void> {
  await db.put("settings", setting.key, setting.value);
}

/** Saves every setting; resolves once all of them are stored. */
export async function saveAll(settings: Setting[]): Promise<void> {
  settings.forEach(async (setting) => {
    await saveSetting(setting);
  });
}
