export type UpdateManagerBusyAction = 'check' | 'download' | 'install';

/**
 * Separates read-only status requests from user-triggered update actions.
 * Cancelling one lifecycle invalidates only its own late responses.
 */
export class UpdateRequestCoordinator {
  #statusGeneration = 0;
  #actionGeneration = 0;

  statusPending = false;
  busy: UpdateManagerBusyAction | null = null;

  beginStatus(): number | null {
    if (this.statusPending || this.busy) return null;
    this.statusPending = true;
    this.#statusGeneration += 1;
    return this.#statusGeneration;
  }

  isStatusCurrent(generation: number): boolean {
    return this.statusPending && generation === this.#statusGeneration;
  }

  finishStatus(generation: number): boolean {
    if (!this.isStatusCurrent(generation)) return false;
    this.statusPending = false;
    return true;
  }

  cancelStatus(): void {
    this.#statusGeneration += 1;
    this.statusPending = false;
  }

  beginAction(action: UpdateManagerBusyAction): number | null {
    if (this.busy) return null;
    this.cancelStatus();
    this.#actionGeneration += 1;
    this.busy = action;
    return this.#actionGeneration;
  }

  isActionCurrent(generation: number): boolean {
    return this.busy !== null && generation === this.#actionGeneration;
  }

  finishAction(generation: number): boolean {
    if (!this.isActionCurrent(generation)) return false;
    this.busy = null;
    return true;
  }

  cancelAction(): void {
    this.#actionGeneration += 1;
    this.busy = null;
  }

  cancelAll(): void {
    this.cancelStatus();
    this.cancelAction();
  }
}
