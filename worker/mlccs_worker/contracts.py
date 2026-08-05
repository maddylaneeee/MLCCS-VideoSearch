class WorkerError(RuntimeError):
    def __init__(self, code: str, summary: str, recoverable: bool = True) -> None:
        super().__init__(summary)
        self.code = code
        self.summary = summary
        self.recoverable = recoverable
