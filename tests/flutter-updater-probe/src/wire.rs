use serde::{Deserialize, Serialize};

use crate::{ErrorKind, ProbeError, Result};

pub const PROTOCOL_VERSION: u32 = 1;
pub const MAX_FRAME_BYTES: usize = 16 * 1024;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ProbeCommand {
    Hello,
    Cancel,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Request {
    protocol_version: u32,
    session: String,
    request_id: u32,
    command: ProbeCommand,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct Response<'a> {
    protocol_version: u32,
    session: &'a str,
    request_id: u32,
    state_sequence: u32,
    state: &'a str,
    message: &'a str,
}

/// The two-message probe deliberately has no install/start command. Session and
/// child identity are distinct: the Windows transport must verify both.
pub struct ProbeSession {
    nonce: String,
    next_request: u32,
}

impl ProbeSession {
    pub fn new(nonce: String) -> Result<Self> {
        if nonce.len() != 32
            || !nonce
                .bytes()
                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        {
            return Err(invalid(
                "session must contain 128 random bits as lowercase hex",
            ));
        }
        Ok(Self {
            nonce,
            next_request: 1,
        })
    }

    /// Input excludes the final LF. Embedded line breaks and extra fields fail.
    pub fn accept(&mut self, frame: &[u8]) -> Result<Vec<u8>> {
        if frame.is_empty()
            || frame.len() > MAX_FRAME_BYTES
            || frame.contains(&b'\n')
            || frame.contains(&b'\r')
        {
            return Err(invalid("invalid frame length or line ending"));
        }
        let request: Request =
            serde_json::from_slice(frame).map_err(|error| invalid(error.to_string()))?;
        let expected = match self.next_request {
            1 => ProbeCommand::Hello,
            2 => ProbeCommand::Cancel,
            _ => return Err(invalid("probe is already complete")),
        };
        if request.protocol_version != PROTOCOL_VERSION
            || request.session != self.nonce
            || request.request_id != self.next_request
            || request.command != expected
        {
            return Err(invalid("protocol/session/request order mismatch"));
        }
        let response = Response {
            protocol_version: PROTOCOL_VERSION,
            session: &self.nonce,
            request_id: self.next_request,
            state_sequence: self.next_request,
            state: if expected == ProbeCommand::Hello {
                "ready"
            } else {
                "cancelled"
            },
            message: if expected == ProbeCommand::Hello {
                "P0 只读探针；不会执行安装或游戏操作"
            } else {
                "P0 探针已取消；未修改插件目录或关闭游戏"
            },
        };
        let mut bytes =
            serde_json::to_vec(&response).map_err(|error| invalid(error.to_string()))?;
        bytes.push(b'\n');
        self.next_request += 1;
        Ok(bytes)
    }
}

fn invalid(message: impl Into<String>) -> ProbeError {
    ProbeError::new(ErrorKind::InvalidArguments, message)
}
