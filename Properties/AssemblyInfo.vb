Imports System.Runtime.CompilerServices

' Der Pruefstand prueft die internen Bildvertraege direkt. Sie bleiben intern, statt nur
' fuer die Diagnose eine oeffentliche und spaeter zu erhaltende API zu schaffen.
<Assembly: InternalsVisibleTo("ImagingPrecisionProbe")>
