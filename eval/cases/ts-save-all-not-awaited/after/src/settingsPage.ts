import { saveAll, Setting } from "./settings";

export async function onSave(form: Setting[], close: () => void, showError: (e: unknown) => void) {
  try {
    await saveAll(form);
    close();
  } catch (e) {
    showError(e);
  }
}
