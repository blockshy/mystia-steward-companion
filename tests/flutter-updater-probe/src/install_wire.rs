//! Separate v2 fixture protocol; v1 remains strictly hello/cancel and read-only.
use crate::status::LegacyInstallStatus;
use crate::wire::MAX_FRAME_BYTES;
use crate::{ErrorKind, ProbeError, Result};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum InstallCommand {
    Hello,
    Start,
    Status,
    Cancel,
    Finish,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Request {
    protocol_version: u32,
    session: String,
    request_id: u32,
    command: InstallCommand,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Response<'a> {
    protocol_version: u32,
    session: &'a str,
    request_id: u32,
    state_sequence: u64,
    state: &'a str,
    message: &'a str,
    progress: u8,
    terminal: bool,
    can_cancel: bool,
}
pub struct InstallSession {
    nonce: String,
    next_request: u32,
    started: bool,
    finished: bool,
}
impl InstallSession {
    pub fn new(nonce: String) -> Result<Self> {
        crate::wire::ProbeSession::new(nonce.clone())?;
        Ok(Self {
            nonce,
            next_request: 1,
            started: false,
            finished: false,
        })
    }
    pub fn accept(&mut self, frame: &[u8], terminal: bool) -> Result<(u32, InstallCommand)> {
        if frame.is_empty()
            || frame.len() > MAX_FRAME_BYTES
            || frame.contains(&b'\n')
            || frame.contains(&b'\r')
        {
            return Err(invalid("invalid fixture frame size/ending"));
        }
        let request: Request =
            serde_json::from_slice(frame).map_err(|error| invalid(error.to_string()))?;
        if request.protocol_version != 2
            || request.session != self.nonce
            || request.request_id != self.next_request
            || self.finished
            || self.next_request > 10000
        {
            return Err(invalid("fixture protocol/session/request mismatch"));
        }
        let allowed = if self.next_request == 1 {
            request.command == InstallCommand::Hello
        } else {
            match request.command {
                InstallCommand::Hello => false,
                InstallCommand::Start => !self.started && !terminal,
                InstallCommand::Finish => terminal,
                // A UI cancellation can race a terminal publication. The
                // shared control returns Finished/TooLate without any write;
                // reply with current truth instead of losing a valid result.
                InstallCommand::Cancel => true,
                InstallCommand::Status => true,
            }
        };
        if !allowed {
            return Err(invalid("fixture command is not admissible in this state"));
        }
        if request.command == InstallCommand::Start {
            self.started = true;
        }
        if request.command == InstallCommand::Finish {
            self.finished = true;
        }
        self.next_request += 1;
        Ok((request.request_id, request.command))
    }
    pub fn reply(
        &self,
        request: u32,
        sequence: u64,
        state: &str,
        status: &LegacyInstallStatus,
        terminal: bool,
        can_cancel: bool,
    ) -> Result<Vec<u8>> {
        let value = Response {
            protocol_version: 2,
            session: &self.nonce,
            request_id: request,
            state_sequence: sequence,
            state,
            message: &status.message,
            progress: status.progress,
            terminal,
            can_cancel,
        };
        let mut bytes = serde_json::to_vec(&value).map_err(|error| invalid(error.to_string()))?;
        if bytes.len() > MAX_FRAME_BYTES {
            return Err(invalid("fixture response too large"));
        }
        bytes.push(b'\n');
        Ok(bytes)
    }
}
fn invalid(message: impl Into<String>) -> ProbeError {
    ProbeError::new(ErrorKind::InvalidArguments, message)
}
