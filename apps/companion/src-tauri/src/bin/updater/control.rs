#![cfg_attr(
    not(any(target_os = "windows", feature = "updater-windows-ui-check", test)),
    allow(dead_code)
)]

use std::sync::{Condvar, Mutex};
use std::time::Duration;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum InstallPhase {
    Preparing,
    Waiting,
    Replacing,
    Finished,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CancelDecision {
    Accepted,
    TooLate,
    Finished,
}

struct State {
    phase: InstallPhase,
    cancelled: bool,
}

/// Cancellation and replacement admission share this lock. A cancelled worker
/// cannot later enter replacement, and an admitted replacement cannot be cancelled.
pub struct InstallControl {
    state: Mutex<State>,
    changed: Condvar,
}

impl Default for InstallControl {
    fn default() -> Self {
        Self {
            state: Mutex::new(State {
                phase: InstallPhase::Preparing,
                cancelled: false,
            }),
            changed: Condvar::new(),
        }
    }
}

impl InstallControl {
    pub fn cancel(&self) -> Result<CancelDecision, String> {
        let mut state = self
            .state
            .lock()
            .map_err(|_| "install control unavailable")?;
        let decision = match state.phase {
            InstallPhase::Preparing | InstallPhase::Waiting => {
                state.cancelled = true;
                CancelDecision::Accepted
            }
            InstallPhase::Replacing => CancelDecision::TooLate,
            InstallPhase::Finished => CancelDecision::Finished,
        };
        self.changed.notify_all();
        Ok(decision)
    }

    pub fn cancelled(&self) -> Result<bool, String> {
        Ok(self
            .state
            .lock()
            .map_err(|_| "install control unavailable")?
            .cancelled)
    }

    pub fn enter_waiting(&self) -> Result<bool, String> {
        let mut state = self
            .state
            .lock()
            .map_err(|_| "install control unavailable")?;
        if state.cancelled {
            return Ok(false);
        }
        if state.phase != InstallPhase::Preparing {
            return Err("invalid installation waiting transition".to_string());
        }
        state.phase = InstallPhase::Waiting;
        Ok(true)
    }

    pub fn begin_replacement(&self) -> Result<bool, String> {
        let mut state = self
            .state
            .lock()
            .map_err(|_| "install control unavailable")?;
        if state.cancelled {
            return Ok(false);
        }
        if state.phase != InstallPhase::Waiting {
            return Err("invalid installation replacement transition".to_string());
        }
        state.phase = InstallPhase::Replacing;
        Ok(true)
    }

    pub fn wait(&self, timeout: Duration) -> Result<(), String> {
        let state = self
            .state
            .lock()
            .map_err(|_| "install control unavailable")?;
        let _guard = self
            .changed
            .wait_timeout_while(state, timeout, |state| !state.cancelled)
            .map_err(|_| "install control unavailable")?;
        Ok(())
    }

    pub fn finish(&self) -> Result<(), String> {
        let mut state = self
            .state
            .lock()
            .map_err(|_| "install control unavailable")?;
        if state.phase == InstallPhase::Finished {
            return Err("installation already finished".to_string());
        }
        state.phase = InstallPhase::Finished;
        self.changed.notify_all();
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{Arc, Barrier};
    use std::thread;

    #[test]
    fn cancellation_and_replacement_have_one_winner() {
        for _ in 0..64 {
            let control = Arc::new(InstallControl::default());
            control.enter_waiting().unwrap();
            let barrier = Arc::new(Barrier::new(3));
            let cancel = {
                let control = control.clone();
                let barrier = barrier.clone();
                thread::spawn(move || {
                    barrier.wait();
                    control.cancel().unwrap()
                })
            };
            let replace = {
                let control = control.clone();
                let barrier = barrier.clone();
                thread::spawn(move || {
                    barrier.wait();
                    control.begin_replacement().unwrap()
                })
            };
            barrier.wait();
            assert_eq!(
                cancel.join().unwrap() == CancelDecision::TooLate,
                replace.join().unwrap()
            );
        }
    }

    #[test]
    fn cancellation_cannot_be_reversed() {
        let control = InstallControl::default();
        control.enter_waiting().unwrap();
        assert_eq!(control.cancel().unwrap(), CancelDecision::Accepted);
        assert_eq!(control.cancel().unwrap(), CancelDecision::Accepted);
        assert!(!control.begin_replacement().unwrap());
        control.finish().unwrap();
        assert_eq!(control.cancel().unwrap(), CancelDecision::Finished);
        assert!(control.finish().is_err());
    }

    #[test]
    fn replacement_rejects_cancellation() {
        let control = InstallControl::default();
        control.enter_waiting().unwrap();
        assert!(control.begin_replacement().unwrap());
        assert_eq!(control.cancel().unwrap(), CancelDecision::TooLate);
    }
}
