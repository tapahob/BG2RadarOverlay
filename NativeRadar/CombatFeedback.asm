; At each original CALL site Swing/Damage already prepared all eight arguments
; and shadow space. Make the observed call on that same stack, then resume.
EXTERN combatFeedback:PROC
EXTERN backstabResume:QWORD, sneakAttackResume:QWORD, meleeResume:QWORD, rangedResume:QWORD

.code
feedbackSite MACRO siteName, resumeAddress
siteName PROC
    mov r9, r15
    call combatFeedback
    jmp QWORD PTR [resumeAddress]
siteName ENDP
ENDM

feedbackSite backstabFeedbackSite, backstabResume
feedbackSite sneakAttackFeedbackSite, sneakAttackResume
feedbackSite meleeIneffectiveSite, meleeResume
feedbackSite rangedIneffectiveSite, rangedResume
END
