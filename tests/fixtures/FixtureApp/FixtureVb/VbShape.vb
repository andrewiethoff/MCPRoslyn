Imports FixtureCore

''' <summary>A Visual Basic implementation of IShape.</summary>
Public Class VbShape
    Implements IShape

    Public Function Area() As Double Implements IShape.Area
        Return 1.0
    End Function
End Class
