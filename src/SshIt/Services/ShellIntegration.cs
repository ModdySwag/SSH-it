namespace SshIt.Services;

/// <summary>
/// Shell integration snippets. Bash is what most hosts run; zsh and fish get the same OSC 133
/// contract through their own hooks (preexec gives a C mark before the command runs, which is
/// more reliable than bash's PS0). The text is kept here, not in the window, so the self-test can
/// assert each snippet carries the marks it promises.
/// </summary>
public static class ShellIntegration
{
    public static readonly string[] Shells = { "Bash", "Zsh", "Fish" };

    public static string FileFor(string shell) => shell switch
    {
        "Zsh" => "~/.zshrc",
        "Fish" => "~/.config/fish/config.fish",
        _ => "~/.bashrc"
    };

    public static string Snippet(string shell) => shell switch
    {
        "Zsh" => Zsh,
        "Fish" => Fish,
        _ => Bash
    };

    /// <summary>What the app can do with the marks once the snippet is in place — one sentence,
    /// because the dialog already carries the how.</summary>
    public static string Description(string shell) =>
        $"Paste this into {FileFor(shell)} on the remote host (or send it to the active session). " +
        "Then SSH-it can group commands into blocks: exit status, duration, copy command or output, " +
        "and jump between prompts. It changes nothing about ssh itself, and the revert is a delete.";

    public const string Bash = """
# SSH-it shell integration (bash).
# Revert: delete this block from ~/.bashrc and open a new shell.
__sshit_last=${HISTCMD:-0}
__sshit_precmd() {
  local status=$?
  if [ "${HISTCMD:-0}" -gt "${__sshit_last:-0}" ]; then
    printf '\033]133;D;%s\007' "$status"
    __sshit_last=${HISTCMD:-0}
  fi
  printf '\033]7;file://%s%s\007' "${HOSTNAME:-localhost}" "$PWD"
  printf '\033]133;A\007'
}
PS0='\[\e]133;C\a\]'
PS1='\[\e]133;B\a\e[K\]'"$PS1"
PROMPT_COMMAND="__sshit_precmd${PROMPT_COMMAND:+;$PROMPT_COMMAND}"
printf '\033]133;A\007'
""";

    public const string Zsh = """
# SSH-it shell integration (zsh).
# Revert: delete this block from ~/.zshrc and open a new shell.
__sshit_preexec() {
  __sshit_ran=1
  printf '\033]133;C\007'
}
__sshit_precmd() {
  local ret=$?
  if [[ -n $__sshit_ran ]]; then
    printf '\033]133;D;%s\007' "$ret"
    unset __sshit_ran
  fi
  printf '\033]7;file://%s%s\007' "${HOST:-localhost}" "$PWD"
  printf '\033]133;A\007'
}
autoload -Uz add-zsh-hook
add-zsh-hook preexec __sshit_preexec
add-zsh-hook precmd __sshit_precmd
PS1=$'%{\e]133;B\a\e[K%}'"$PS1"
""";

    public const string Fish = """
# SSH-it shell integration (fish).
# Fish 4.0 and newer emit the OSC 133 marks itself — there is nothing to paste.
# On older fish there is no hook, so command blocks are not available in that shell.
""";
}
