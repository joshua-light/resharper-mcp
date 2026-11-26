# Sourced by build-plugin.sh and install-rider.sh. Gradle needs a JDK; a machine set up only for Rider
# has none on PATH, so fall back to the JBR that Rider bundles. An explicit JAVA_HOME always wins.
if [ -z "${JAVA_HOME:-}" ] && ! command -v java >/dev/null 2>&1; then
    # Globs, not `ls`: Toolbox install dirs contain spaces ("JetBrains Rider-2025.3.3").
    shopt -s nullglob
    _jbr_candidates=(
        "/Applications/Rider.app/Contents/jbr/Contents/Home"
        "$HOME"/.local/share/JetBrains/*Rider*/jbr
        "$HOME"/.local/share/JetBrains/Toolbox/apps/rider/jbr
        /opt/*[Rr]ider*/jbr
        /snap/rider/current/jbr
    )
    shopt -u nullglob
    # Newest Rider first, so a leftover old install doesn't supply an outdated JBR.
    for ((_i = ${#_jbr_candidates[@]} - 1; _i >= 0; _i--)); do
        if [ -x "${_jbr_candidates[_i]}/bin/java" ]; then
            export JAVA_HOME="${_jbr_candidates[_i]}"
            break
        fi
    done
    unset _jbr_candidates _i
fi
